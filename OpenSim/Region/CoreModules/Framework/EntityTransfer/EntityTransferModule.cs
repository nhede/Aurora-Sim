﻿/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading;

using OpenSim.Framework;
using OpenSim.Framework.Capabilities;
using OpenSim.Framework.Client;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

using GridRegion = OpenSim.Services.Interfaces.GridRegion;

using OpenMetaverse;
using log4net;
using Nini.Config;

namespace OpenSim.Region.CoreModules.Framework.EntityTransfer
{
    public class EntityTransferModule : ISharedRegionModule, IEntityTransferModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        protected bool m_Enabled = false;
        protected List<Scene> m_scenes = new List<Scene>();
        protected List<UUID> m_agentsInTransit;
        protected List<UUID> m_cancelingAgents;

        #region ISharedRegionModule

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public virtual string Name
        {
            get { return "BasicEntityTransferModule"; }
        }

        public virtual void Initialise(IConfigSource source)
        {
            IConfig moduleConfig = source.Configs["Modules"];
            if (moduleConfig != null)
            {
                string name = moduleConfig.GetString("EntityTransferModule", "");
                if (name == Name)
                {
                    m_agentsInTransit = new List<UUID>();
                    m_cancelingAgents = new List<UUID>();
                    m_Enabled = true;
                    //m_log.InfoFormat("[ENTITY TRANSFER MODULE]: {0} enabled.", Name);
                }
            }
        }

        public virtual void PostInitialise()
        {
        }

        public virtual void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            m_scenes.Add(scene);

            scene.RegisterModuleInterface<IEntityTransferModule>(this);
            scene.EventManager.OnNewClient += OnNewClient;
            scene.EventManager.OnClosingClient += OnClosingClient;
        }

        public virtual void Close()
        {
        }

        public virtual void RemoveRegion(Scene scene)
        {
            if (!m_Enabled)
                return;
            m_scenes.Remove(scene);

            scene.UnregisterModuleInterface<IEntityTransferModule>(this);
            scene.EventManager.OnNewClient -= OnNewClient;
            scene.EventManager.OnClosingClient -= OnClosingClient;
        }

        public virtual void RegionLoaded(Scene scene)
        {
            if (!m_Enabled)
                return;

        }


        #endregion

        #region Agent Teleports

        public virtual void Teleport(ScenePresence sp, ulong regionHandle, Vector3 position, Vector3 lookAt, uint teleportFlags)
        {
            string reason = "";
            if (!sp.Scene.Permissions.CanTeleport(sp.UUID, position, sp.Scene.AuthenticateHandler.GetAgentCircuitData(sp.UUID), out position, out reason))
            {
                sp.ControllingClient.SendTeleportFailed(reason);
                return;
            }

            sp.ControllingClient.SendTeleportStart(teleportFlags);
            sp.ControllingClient.SendTeleportProgress(teleportFlags, "requesting");
            //sp.ControllingClient.SendTeleportProgress("resolving");

            IEventQueueService eq = sp.Scene.RequestModuleInterface<IEventQueueService>();

            // Reset animations; the viewer does that in teleports.
            if(sp.Animator != null)
                sp.Animator.ResetAnimations();

            try
            {
                if (regionHandle == sp.Scene.RegionInfo.RegionHandle)
                {
                    // m_log.DebugFormat(
                    //    "[ENTITY TRANSFER MODULE]: RequestTeleportToLocation {0} within {1}",
                    //    position, sp.Scene.RegionInfo.RegionName);

                    // Teleport within the same region
                    if (IsOutsideRegion(sp.Scene, position) || position.Z < 0)
                    {
                        Vector3 emergencyPos = new Vector3(128, 128, 128);

                        m_log.WarnFormat(
                            "[ENTITY TRANSFER MODULE]: RequestTeleportToLocation() was given an illegal position of {0} for avatar {1}, {2}.  Substituting {3}",
                            position, sp.Name, sp.UUID, emergencyPos);
                        position = emergencyPos;
                    }

                    float localAVHeight = sp.Appearance.AvatarHeight;
                    float posZLimit = 22;

                    // TODO: Check other Scene HeightField
                    if (position.X > 0 && position.X <= (int)Constants.RegionSize && position.Y > 0 && position.Y <= (int)Constants.RegionSize)
                    {
                        ITerrainChannel heightmap = sp.Scene.RequestModuleInterface<ITerrainChannel>();
                        posZLimit = (float)heightmap[(int)position.X, (int)position.Y];
                    }

                    float newPosZ = posZLimit + localAVHeight;
                    if (posZLimit >= (position.Z - (localAVHeight / 2)) && !(Single.IsInfinity(newPosZ) || Single.IsNaN(newPosZ)))
                    {
                        position.Z = newPosZ;
                    }

                    sp.ControllingClient.SendTeleportStart(teleportFlags);

                    sp.ControllingClient.SendLocalTeleport(position, lookAt, teleportFlags);
                    sp.Teleport(position);
                }
                else // Another region possibly in another simulator
                {
                    uint x = 0, y = 0;
                    Utils.LongToUInts(regionHandle, out x, out y);
                    GridRegion reg = sp.Scene.GridService.GetRegionByPosition(sp.Scene.RegionInfo.ScopeID, (int)x, (int)y);

                    if (reg != null)
                    {
                        GridRegion finalDestination = GetFinalDestination(reg);
                        if (finalDestination == null)
                        {
                            m_log.DebugFormat("[ENTITY TRANSFER MODULE]: Final destination is having problems. Unable to teleport agent.");
                            sp.ControllingClient.SendTeleportFailed("Problem at destination");
                            return;
                        }
                        //m_log.DebugFormat("[ENTITY TRANSFER MODULE]: Final destination is x={0} y={1} uuid={2}",
                        //    finalDestination.RegionLocX / Constants.RegionSize, finalDestination.RegionLocY / Constants.RegionSize, finalDestination.RegionID);

                        // Check that these are not the same coordinates
                        if (finalDestination.RegionLocX == sp.Scene.RegionInfo.RegionLocX &&
                            finalDestination.RegionLocY == sp.Scene.RegionInfo.RegionLocY)
                        {
                            // Can't do. Viewer crashes
                            sp.ControllingClient.SendTeleportFailed("Space warp! You would crash. Move to a different region and try again.");
                            return;
                        }

                        //
                        // This is it
                        //
                        DoTeleport(sp, reg, finalDestination, position, lookAt, teleportFlags, eq);
                        //
                        //
                        //
                    }
                    else
                    {
                        // TP to a place that doesn't exist (anymore)
                        // Inform the viewer about that
                        sp.ControllingClient.SendTeleportFailed("The region you tried to teleport to doesn't exist anymore");

                        // and set the map-tile to '(Offline)'
                        uint regX, regY;
                        Utils.LongToUInts(regionHandle, out regX, out regY);

                        MapBlockData block = new MapBlockData();
                        block.X = (ushort)(regX / Constants.RegionSize);
                        block.Y = (ushort)(regY / Constants.RegionSize);
                        block.Access = 254; // == not there

                        List<MapBlockData> blocks = new List<MapBlockData>();
                        blocks.Add(block);
                        sp.ControllingClient.SendMapBlock(blocks, 0);
                    }
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ENTITY TRANSFER MODULE]: Exception on teleport: {0}\n{1}", e.Message, e.StackTrace);
                sp.ControllingClient.SendTeleportFailed("Internal error");
            }
        }

        public virtual void DoTeleport(ScenePresence sp, GridRegion reg, GridRegion finalDestination, Vector3 position, Vector3 lookAt, uint teleportFlags, IEventQueueService eq)
        {
            sp.ControllingClient.SendTeleportProgress(teleportFlags, "sending_dest");
            if (reg == null || finalDestination == null)
            {
                sp.ControllingClient.SendTeleportFailed("Unable to locate destination");
                return;
            }

            m_log.DebugFormat(
                "[ENTITY TRANSFER MODULE]: Request Teleport to {0}:{1}:{2}/{3}",
                reg.ExternalHostName, reg.HttpPort, finalDestination.RegionName, position);

            uint newRegionX = (uint)(reg.RegionHandle >> 40);
            uint newRegionY = (((uint)(reg.RegionHandle)) >> 8);
            uint oldRegionX = (uint)(sp.Scene.RegionInfo.RegionHandle >> 40);
            uint oldRegionY = (((uint)(sp.Scene.RegionInfo.RegionHandle)) >> 8);

            ulong destinationHandle = finalDestination.RegionHandle;

            // Let's do DNS resolution only once in this process, please!
            // This may be a costly operation. The reg.ExternalEndPoint field is not a passive field,
            // it's actually doing a lot of work.
            IPEndPoint endPoint = finalDestination.ExternalEndPoint;
            if (endPoint.Address != null)
            {
                sp.ControllingClient.SendTeleportProgress(teleportFlags, "arriving");
                    
                if (m_cancelingAgents.Contains(sp.UUID))
                {
                    Cancel(sp);
                    return;
                }
                // Fixing a bug where teleporting while sitting results in the avatar ending up removed from
                // both regions
                if (sp.ParentID != UUID.Zero)
                    sp.StandUp(true);

                if (!sp.ValidateAttachments())
                {
                    sp.ControllingClient.SendTeleportProgress(teleportFlags, "missing_attach_tport");
                    sp.ControllingClient.SendTeleportFailed("Inconsistent attachment state");
                    return;
                }

                string capsPath = String.Empty;

                AgentCircuitData currentAgentCircuit = sp.Scene.AuthenticateHandler.GetAgentCircuitData(sp.ControllingClient.CircuitCode);
                AgentCircuitData agentCircuit = sp.ControllingClient.RequestClientInfo();
                agentCircuit.startpos = position;
                agentCircuit.child = true;
                agentCircuit.Appearance = sp.Appearance;
                if (currentAgentCircuit != null)
                {
                    agentCircuit.ServiceURLs = currentAgentCircuit.ServiceURLs;
                    agentCircuit.IPAddress = currentAgentCircuit.IPAddress;
                    agentCircuit.Viewer = currentAgentCircuit.Viewer;
                    agentCircuit.Channel = currentAgentCircuit.Channel;
                    agentCircuit.Mac = currentAgentCircuit.Mac;
                    agentCircuit.Id0 = currentAgentCircuit.Id0;
                }

                if (NeedsNewAgent(oldRegionX, newRegionX, oldRegionY, newRegionY))
                {
                    // brand new agent, let's create a new caps seed
                    agentCircuit.CapsPath = CapsUtil.GetRandomCapsObjectPath();
                }

                string reason = String.Empty;
                // Let's create an agent there if one doesn't exist yet. 
                bool logout = false;
                if (!CreateAgent(sp, reg, finalDestination, agentCircuit, teleportFlags, out reason, out logout))
                {
                    sp.ControllingClient.SendTeleportFailed(String.Format("Destination refused: {0}",
                                                                              reason));
                    return;
                }

                // OK, it got this agent. Let's close some child agents
                INeighborService neighborService = sp.Scene.RequestModuleInterface<INeighborService>();
                if (neighborService != null)
                    neighborService.CloseNeighborAgents(newRegionX, newRegionY, sp.UUID, sp.Scene.RegionInfo.RegionID);

                if (NeedsNewAgent(oldRegionX, newRegionX, oldRegionY, newRegionY))
                {
                    //sp.ControllingClient.SendTeleportProgress(teleportFlags, "Creating agent...");

                    #region IP Translation for NAT
                    IClientIPEndpoint ipepClient;
                    if (sp.ClientView.TryGet(out ipepClient))
                    {
                        capsPath
                            = "http://"
                              + NetworkUtil.GetHostFor(ipepClient.EndPoint, finalDestination.ExternalHostName)
                              + ":"
                              + finalDestination.HttpPort
                              + CapsUtil.GetCapsSeedPath(agentCircuit.CapsPath);
                    }
                    else
                    {
                        capsPath
                            = "http://"
                              + finalDestination.ExternalHostName
                              + ":"
                              + finalDestination.HttpPort
                              + CapsUtil.GetCapsSeedPath(agentCircuit.CapsPath);
                    }
                    #endregion

                    if (eq != null)
                    {
                        #region IP Translation for NAT
                        // Uses ipepClient above
                        if (sp.ClientView.TryGet(out ipepClient))
                        {
                            endPoint.Address = NetworkUtil.GetIPFor(ipepClient.EndPoint, endPoint.Address);
                        }
                        #endregion

                        eq.EnableSimulator(destinationHandle, endPoint, sp.UUID, sp.Scene.RegionInfo.RegionHandle);

                        // ES makes the client send a UseCircuitCode message to the destination, 
                        // which triggers a bunch of things there.
                        // So let's wait
                        Thread.Sleep(200);

                        eq.EstablishAgentCommunication(sp.UUID, destinationHandle, endPoint, capsPath, sp.Scene.RegionInfo.RegionHandle);

                    }
                    else
                    {
                        sp.ControllingClient.InformClientOfNeighbor(destinationHandle, endPoint);
                    }
                }
                else
                {
                    ICapabilitiesModule module = sp.Scene.RequestModuleInterface<ICapabilitiesModule>();
                    if (module != null)
                        agentCircuit.CapsPath = module.GetChildSeed(sp.UUID, reg.RegionHandle);
                    capsPath = "http://" + finalDestination.ExternalHostName + ":" + finalDestination.HttpPort
                                + "/CAPS/" + agentCircuit.CapsPath + "0000/";
                }

                if (m_cancelingAgents.Contains(sp.UUID))
                {
                    Cancel(sp);
                    return;
                }

                // Let's send a full update of the agent. This is a synchronous call.
                AgentData agent = new AgentData();
                sp.CopyTo(agent);
                agent.Position = position;
                SetCallbackURL(agent, sp.Scene.RegionInfo);

                if (!UpdateAgent(reg, finalDestination, agent))
                {
                    // Region doesn't take it
                    Fail(sp, finalDestination);
                    return;
                }

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Sending new CAPS seed url {0} to client {1}", capsPath, sp.UUID);

                //Set the agent in transit before we send the event
                SetInTransit(sp.UUID);

                if (eq != null)
                {
                    eq.TeleportFinishEvent(destinationHandle, 13, endPoint,
                                           4, teleportFlags, capsPath, sp.UUID, teleportFlags, sp.Scene.RegionInfo.RegionHandle);
                }
                else
                {
                    sp.ControllingClient.SendRegionTeleport(destinationHandle, 13, endPoint, 4,
                                                                teleportFlags, capsPath);
                }

                // Let's set this to true tentatively. This does not trigger OnChildAgent
                sp.IsChildAgent = true;

                // TeleportFinish makes the client send CompleteMovementIntoRegion (at the destination), which
                // trigers a whole shebang of things there, including MakeRoot. So let's wait for confirmation
                // that the client contacted the destination before we send the attachments and close things here.

                //OpenSim sucks at callbacks, disable it for now
                if (!WaitForCallback(sp.UUID))
                {
                    //Make sure the client hasn't TPed back in this time.
                    ScenePresence SP = sp.Scene.GetScenePresence(sp.UUID);
                    if (SP != null && SP.IsChildAgent)
                    {
                        //Disabling until this actually helps and doesn't kill clients
                        Fail(sp, finalDestination);
                        return;
                    }
                    else if (SP == null)
                    {
                        //Err.. this happens somehow.

                        //If they canceled too late, remove them so the next tp does not fail.
                        if (m_cancelingAgents.Contains(sp.UUID))
                            m_cancelingAgents.Remove(sp.UUID);
                        return;
                    }
                }

                //Make sure the client hasn't TPed back in this time.
                ScenePresence newSP = sp.Scene.GetScenePresence(sp.UUID);
                if (newSP != null && !newSP.IsChildAgent)
                {
                    //They are root again, don't cross them!
                    return;
                }
                else if (newSP == null)
                {
                    //Err.. this happens somehow.

                    //If they canceled too late, remove them so the next tp does not fail.
                    if (m_cancelingAgents.Contains(sp.UUID))
                        m_cancelingAgents.Remove(sp.UUID);
                    return;
                }

                // CrossAttachmentsIntoNewRegion is a synchronous call. We shouldn't need to wait after it
                CrossAttachmentsIntoNewRegion(finalDestination, sp);

                // Well, this is it. The agent is over there.

                KillEntity(sp.Scene, sp);

                // May need to logout or other cleanup
                AgentHasMovedAway(sp.ControllingClient.SessionId, logout);

                // Now let's make it officially a child agent
                sp.MakeChildAgent();

                sp.Scene.CleanDroppedAttachments();

                // Finally, let's close this previously-known-as-root agent, when the jump is outside the view zone

                if (NeedsClosing(oldRegionX, newRegionX, oldRegionY, newRegionY, reg))
                {
                    Thread.Sleep(5000);
                    sp.Close();
                    sp.Scene.IncomingCloseAgent(sp.UUID);
                }
                else
                    // now we have a child agent in this region. 
                    sp.Reset();

                //If they canceled too late, remove them so the next tp does not fail.
                if (m_cancelingAgents.Contains(sp.UUID))
                    m_cancelingAgents.Remove(sp.UUID);
            }
            else
            {
                sp.ControllingClient.SendTeleportFailed("Remote Region appears to be down");
            }
        }

        private void Cancel(ScenePresence sp)
        {
            m_cancelingAgents.Remove(sp.UUID);

            // Client never contacted destination. Let's restore everything back
            sp.ControllingClient.SendTeleportFailed("You canceled the tp.");

            // Fail. Reset it back
            sp.IsChildAgent = false;

            ResetFromTransit(sp.UUID);

            EnableChildAgents(sp);
        }

        private void Fail(ScenePresence sp, GridRegion finalDestination)
        {
            // Client never contacted destination. Let's restore everything back
            sp.ControllingClient.SendTeleportFailed("Problems connecting to destination.");

            // Fail. Reset it back
            sp.IsChildAgent = false;

            ResetFromTransit(sp.UUID);

            EnableChildAgents(sp);

            //If they canceled too late, remove them so the next tp does not fail.
            if (m_cancelingAgents.Contains(sp.UUID))
                m_cancelingAgents.Remove(sp.UUID);

            // Finally, kill the agent we just created at the destination.

            //Don't do this right now since we are killing way too many legitimate agents whose callbacks failed
            //m_aScene.SimulationService.CloseAgent(finalDestination, sp.UUID);

        }

        protected virtual bool CreateAgent(ScenePresence sp, GridRegion reg, GridRegion finalDestination, AgentCircuitData agentCircuit, uint teleportFlags, out string reason, out bool logout)
        {
            logout = false;
            //We use tryGet as we can't be completely sure that the Scene isn't null or is incorrect
            return TryGetScene(sp.Scene.RegionInfo.RegionID).SimulationService.CreateAgent(finalDestination, agentCircuit, teleportFlags, out reason);
        }

        protected virtual bool UpdateAgent(GridRegion reg, GridRegion finalDestination, AgentData agent)
        {
            return TryGetScene(reg.RegionID).SimulationService.UpdateAgent(finalDestination, agent);
        }

        protected virtual void SetCallbackURL(AgentData agent, RegionInfo region)
        {
            agent.CallbackURI = "http://" + region.ExternalHostName + ":" + region.HttpPort +
                "/agent/" + agent.AgentID.ToString() + "/" + region.RegionID.ToString() + "/release/";

        }

        protected virtual void AgentHasMovedAway(UUID sessionID, bool logout)
        {
        }

        protected void KillEntity(Scene scene, ISceneEntity entity)
        {
            scene.ForEachClient(delegate(IClientAPI client)
            {
                client.SendKillObject(scene.RegionInfo.RegionHandle, new ISceneEntity[] { entity });
            });
        }

        protected void KillEntities(Scene scene, ISceneEntity[] grp)
        {
            scene.ForEachClient(delegate(IClientAPI client)
            {
                client.SendKillObject(scene.RegionInfo.RegionHandle, grp);
            });
        }

        protected virtual GridRegion GetFinalDestination(GridRegion region)
        {
            return region;
        }

        protected virtual bool NeedsNewAgent(uint oldRegionX, uint newRegionX, uint oldRegionY, uint newRegionY)
        {
            INeighborService neighborService = m_scenes[0].RequestModuleInterface<INeighborService>();
            if (neighborService != null)
                return neighborService.IsOutsideView(oldRegionX, newRegionX, oldRegionY, newRegionY);
            return false;
        }

        protected virtual bool NeedsClosing(uint oldRegionX, uint newRegionX, uint oldRegionY, uint newRegionY, GridRegion reg)
        {
            INeighborService neighborService = m_scenes[0].RequestModuleInterface<INeighborService>();
            if (neighborService != null)
                return neighborService.IsOutsideView(oldRegionX, newRegionX, oldRegionY, newRegionY);
            return false;
        }

        protected virtual bool IsOutsideRegion(Scene s, Vector3 pos)
        {
            if (s.TestBorderCross(pos, Cardinals.N))
                return true;
            if (s.TestBorderCross(pos, Cardinals.S))
                return true;
            if (s.TestBorderCross(pos, Cardinals.E))
                return true;
            if (s.TestBorderCross(pos, Cardinals.W))
                return true;

            return false;
        }


        #endregion

        #region Client Events

        protected virtual void OnNewClient(IClientAPI client)
        {
            client.OnTeleportHomeRequest += TeleportHome;
            client.OnTeleportCancel += RequestTeleportCancel;
            client.OnTeleportLocationRequest += RequestTeleportLocation;
            client.OnTeleportLandmarkRequest += RequestTeleportLandmark;
        }

        protected virtual void OnClosingClient(IClientAPI client)
        {
            client.OnTeleportHomeRequest -= TeleportHome;
            client.OnTeleportCancel -= RequestTeleportCancel;
            client.OnTeleportLocationRequest -= RequestTeleportLocation;
            client.OnTeleportLandmarkRequest -= RequestTeleportLandmark;
        }

        public void RequestTeleportCancel(IClientAPI client)
        {
            CancelTeleport(client.AgentId);
        }

        /// <summary>
        /// Tries to teleport agent to other region.
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="regionName"></param>
        /// <param name="position"></param>
        /// <param name="lookAt"></param>
        /// <param name="teleportFlags"></param>
        public void RequestTeleportLocation(IClientAPI remoteClient, string regionName, Vector3 position,
                                            Vector3 lookat, uint teleportFlags)
        {
            GridRegion regionInfo = remoteClient.Scene.RequestModuleInterface<IGridService>().GetRegionByName(UUID.Zero, regionName);
            if (regionInfo == null)
            {
                // can't find the region: Tell viewer and abort
                remoteClient.SendTeleportFailed("The region '" + regionName + "' could not be found.");
                return;
            }

            RequestTeleportLocation(remoteClient, regionInfo.RegionHandle, position, lookat, teleportFlags);
        }

        /// <summary>
        /// Tries to teleport agent to other region.
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="regionHandle"></param>
        /// <param name="position"></param>
        /// <param name="lookAt"></param>
        /// <param name="teleportFlags"></param>
        public void RequestTeleportLocation(IClientAPI remoteClient, ulong regionHandle, Vector3 position,
                                            Vector3 lookAt, uint teleportFlags)
        {
            Scene scene = (Scene)remoteClient.Scene;
            ScenePresence sp = scene.GetScenePresence(remoteClient.AgentId);
            if (sp != null)
            {
                uint regionX = scene.RegionInfo.RegionLocX;
                uint regionY = scene.RegionInfo.RegionLocY;

                Utils.LongToUInts(regionHandle, out regionX, out regionY);

                int shiftx = (int)regionX - (int)scene.RegionInfo.RegionLocX * (int)Constants.RegionSize;
                int shifty = (int)regionY - (int)scene.RegionInfo.RegionLocY * (int)Constants.RegionSize;

                position.X += shiftx;
                position.Y += shifty;

                bool result = false;

                if (scene.TestBorderCross(position, Cardinals.N))
                    result = true;

                if (scene.TestBorderCross(position, Cardinals.S))
                    result = true;

                if (scene.TestBorderCross(position, Cardinals.E))
                    result = true;

                if (scene.TestBorderCross(position, Cardinals.W))
                    result = true;

                // bordercross if position is outside of region

                if (!result)
                    regionHandle = scene.RegionInfo.RegionHandle;
                else
                {
                    // not in this region, undo the shift!
                    position.X -= shiftx;
                    position.Y -= shifty;
                }

                Teleport(sp, regionHandle, position, lookAt, teleportFlags);
            }
        }

        /// <summary>
        /// Tries to teleport agent to landmark.
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="regionHandle"></param>
        /// <param name="position"></param>
        public void RequestTeleportLandmark(IClientAPI remoteClient, UUID regionID, Vector3 position)
        {
            GridRegion info = remoteClient.Scene.RequestModuleInterface<IGridService>().GetRegionByUUID(UUID.Zero, regionID);

            if (info == null)
            {
                // can't find the region: Tell viewer and abort
                remoteClient.SendTeleportFailed("The teleport destination could not be found.");
                return;
            }

            RequestTeleportLocation(remoteClient, info.RegionHandle, position, Vector3.Zero, (uint)(TeleportFlags.SetLastToTarget | TeleportFlags.ViaLandmark));
        }

        #endregion

        #region Teleport Home

        public virtual void TeleportHome(UUID id, IClientAPI client)
        {
            //m_log.DebugFormat("[ENTITY TRANSFER MODULE]: Request to teleport {0} {1} home", client.FirstName, client.LastName);

            //OpenSim.Services.Interfaces.PresenceInfo pinfo = m_aScene.PresenceService.GetAgent(client.SessionId);
            GridUserInfo uinfo = GetScene(client.Scene.RegionInfo.RegionID).GridUserService.GetGridUserInfo(client.AgentId.ToString());

            if (uinfo != null)
            {
                GridRegion regionInfo = GetScene(client.Scene.RegionInfo.RegionID).GridService.GetRegionByUUID(UUID.Zero, uinfo.HomeRegionID);
                if (regionInfo == null)
                {
                    // can't find the Home region: Tell viewer and abort
                    client.SendTeleportFailed("Your home region could not be found.");
                    return;
                }
                m_log.DebugFormat("[ENTITY TRANSFER MODULE]: User's home region is {0} {1} ({2}-{3})",
                    regionInfo.RegionName, regionInfo.RegionID, regionInfo.RegionLocX / Constants.RegionSize, regionInfo.RegionLocY / Constants.RegionSize);

                RequestTeleportLocation(
                    client, regionInfo.RegionHandle, uinfo.HomePosition, uinfo.HomeLookAt,
                    (uint)(Constants.TeleportFlags.SetLastToTarget | Constants.TeleportFlags.ViaHome));
            }
            else
            {
                //Default region time...
                List<GridRegion> Regions = GetScene(client.Scene.RegionInfo.RegionID).GridService.GetDefaultRegions(UUID.Zero);
                if (Regions.Count != 0)
                {
                    m_log.DebugFormat("[ENTITY TRANSFER MODULE]: User's home region was not found, using {0} {1} ({2}-{3})",
                        Regions[0].RegionName, Regions[0].RegionID, Regions[0].RegionLocX / Constants.RegionSize, Regions[0].RegionLocY / Constants.RegionSize);

                    RequestTeleportLocation(
                        client, Regions[0].RegionHandle, new Vector3(128, 128, 25), new Vector3(128, 128, 128),
                        (uint)(Constants.TeleportFlags.SetLastToTarget | Constants.TeleportFlags.ViaHome));
                }
            }
        }

        #endregion

        #region Agent Crossings

        public virtual void Cross(ScenePresence agent, bool isFlying)
        {
            Scene scene = agent.Scene;
            Vector3 pos = agent.AbsolutePosition;
            Vector3 newpos = new Vector3(pos.X, pos.Y, pos.Z);
            uint neighbourx = scene.RegionInfo.RegionLocX;
            uint neighboury = scene.RegionInfo.RegionLocY;
            const float boundaryDistance = 1.7f;
            Vector3 northCross = new Vector3(0, boundaryDistance, 0);
            Vector3 southCross = new Vector3(0, -1 * boundaryDistance, 0);
            Vector3 eastCross = new Vector3(boundaryDistance, 0, 0);
            Vector3 westCross = new Vector3(-1 * boundaryDistance, 0, 0);

            // distance to edge that will trigger crossing


            // distance into new region to place avatar
            const float enterDistance = 0.5f;

            if (scene.TestBorderCross(pos + westCross, Cardinals.W))
            {
                if (scene.TestBorderCross(pos + northCross, Cardinals.N))
                {
                    Border b = scene.GetCrossedBorder(pos + northCross, Cardinals.N);
                    neighboury += (uint)(int)(b.BorderLine.Z / (int)Constants.RegionSize);
                }
                else if (scene.TestBorderCross(pos + southCross, Cardinals.S))
                {
                    Border b = scene.GetCrossedBorder(pos + southCross, Cardinals.S);
                    if (b.TriggerRegionX == 0 && b.TriggerRegionY == 0)
                    {
                        neighboury--;
                        newpos.Y = Constants.RegionSize - enterDistance;
                    }
                    else
                    {
                        neighboury = b.TriggerRegionY;
                        neighbourx = b.TriggerRegionX;

                        Vector3 newposition = pos;
                        newposition.X += (scene.RegionInfo.RegionLocX - neighbourx) * Constants.RegionSize;
                        newposition.Y += (scene.RegionInfo.RegionLocY - neighboury) * Constants.RegionSize;
                        agent.ControllingClient.SendAgentAlertMessage(
                            String.Format("Moving you to region {0},{1}", neighbourx, neighboury), false);
                        InformClientToInitateTeleportToLocation(agent, neighbourx, neighboury, newposition, scene);
                        return;
                    }
                }

                Border ba = scene.GetCrossedBorder(pos + westCross, Cardinals.W);
                if (ba.TriggerRegionX == 0 && ba.TriggerRegionY == 0)
                {
                    neighbourx--;
                    newpos.X = Constants.RegionSize - enterDistance;
                }
                else
                {
                    neighboury = ba.TriggerRegionY;
                    neighbourx = ba.TriggerRegionX;


                    Vector3 newposition = pos;
                    newposition.X += (scene.RegionInfo.RegionLocX - neighbourx) * Constants.RegionSize;
                    newposition.Y += (scene.RegionInfo.RegionLocY - neighboury) * Constants.RegionSize;
                    agent.ControllingClient.SendAgentAlertMessage(
                            String.Format("Moving you to region {0},{1}", neighbourx, neighboury), false);
                    InformClientToInitateTeleportToLocation(agent, neighbourx, neighboury, newposition, scene);


                    return;
                }

            }
            else if (scene.TestBorderCross(pos + eastCross, Cardinals.E))
            {
                Border b = scene.GetCrossedBorder(pos + eastCross, Cardinals.E);
                neighbourx += (uint)(int)(b.BorderLine.Z / (int)Constants.RegionSize);
                newpos.X = enterDistance;

                if (scene.TestBorderCross(pos + southCross, Cardinals.S))
                {
                    Border ba = scene.GetCrossedBorder(pos + southCross, Cardinals.S);
                    if (ba.TriggerRegionX == 0 && ba.TriggerRegionY == 0)
                    {
                        neighboury--;
                        newpos.Y = Constants.RegionSize - enterDistance;
                    }
                    else
                    {
                        neighboury = ba.TriggerRegionY;
                        neighbourx = ba.TriggerRegionX;
                        Vector3 newposition = pos;
                        newposition.X += (scene.RegionInfo.RegionLocX - neighbourx) * Constants.RegionSize;
                        newposition.Y += (scene.RegionInfo.RegionLocY - neighboury) * Constants.RegionSize;
                        agent.ControllingClient.SendAgentAlertMessage(
                            String.Format("Moving you to region {0},{1}", neighbourx, neighboury), false);
                        InformClientToInitateTeleportToLocation(agent, neighbourx, neighboury, newposition, scene);
                        return;
                    }
                }
                else if (scene.TestBorderCross(pos + northCross, Cardinals.N))
                {
                    Border c = scene.GetCrossedBorder(pos + northCross, Cardinals.N);
                    neighboury += (uint)(int)(c.BorderLine.Z / (int)Constants.RegionSize);
                    newpos.Y = enterDistance;
                }


            }
            else if (scene.TestBorderCross(pos + southCross, Cardinals.S))
            {
                Border b = scene.GetCrossedBorder(pos + southCross, Cardinals.S);
                if (b.TriggerRegionX == 0 && b.TriggerRegionY == 0)
                {
                    neighboury--;
                    newpos.Y = Constants.RegionSize - enterDistance;
                }
                else
                {
                    neighboury = b.TriggerRegionY;
                    neighbourx = b.TriggerRegionX;
                    Vector3 newposition = pos;
                    newposition.X += (scene.RegionInfo.RegionLocX - neighbourx) * Constants.RegionSize;
                    newposition.Y += (scene.RegionInfo.RegionLocY - neighboury) * Constants.RegionSize;
                    agent.ControllingClient.SendAgentAlertMessage(
                            String.Format("Moving you to region {0},{1}", neighbourx, neighboury), false);
                    InformClientToInitateTeleportToLocation(agent, neighbourx, neighboury, newposition, scene);
                    return;
                }
            }
            else if (scene.TestBorderCross(pos + northCross, Cardinals.N))
            {

                Border b = scene.GetCrossedBorder(pos + northCross, Cardinals.N);
                neighboury += (uint)(int)(b.BorderLine.Z / (int)Constants.RegionSize);
                newpos.Y = enterDistance;
            }

            /*

            if (pos.X < boundaryDistance) //West
            {
                neighbourx--;
                newpos.X = Constants.RegionSize - enterDistance;
            }
            else if (pos.X > Constants.RegionSize - boundaryDistance) // East
            {
                neighbourx++;
                newpos.X = enterDistance;
            }

            if (pos.Y < boundaryDistance) // South
            {
                neighboury--;
                newpos.Y = Constants.RegionSize - enterDistance;
            }
            else if (pos.Y > Constants.RegionSize - boundaryDistance) // North
            {
                neighboury++;
                newpos.Y = enterDistance;
            }
            */

            CrossAgentToNewRegionDelegate d = CrossAgentToNewRegionAsync;
            d.BeginInvoke(agent, newpos, neighbourx, neighboury, isFlying, CrossAgentToNewRegionCompleted, d);

        }


        public delegate void InformClientToInitateTeleportToLocationDelegate(ScenePresence agent, uint regionX, uint regionY,
                                                            Vector3 position,
                                                            Scene initiatingScene);

        protected void InformClientToInitateTeleportToLocation(ScenePresence agent, uint regionX, uint regionY, Vector3 position, Scene initiatingScene)
        {

            // This assumes that we know what our neighbors are.

            InformClientToInitateTeleportToLocationDelegate d = InformClientToInitiateTeleportToLocationAsync;
            d.BeginInvoke(agent, regionX, regionY, position, initiatingScene,
                          InformClientToInitiateTeleportToLocationCompleted,
                          d);
        }

        public void InformClientToInitiateTeleportToLocationAsync(ScenePresence agent, uint regionX, uint regionY, Vector3 position,
            Scene initiatingScene)
        {
            Thread.Sleep(10000);
            IMessageTransferModule im = initiatingScene.RequestModuleInterface<IMessageTransferModule>();
            if (im != null)
            {
                UUID gotoLocation = Util.BuildFakeParcelID(
                    Util.UIntsToLong(
                                              (regionX *
                                               (uint)Constants.RegionSize),
                                              (regionY *
                                               (uint)Constants.RegionSize)),
                    (uint)(int)position.X,
                    (uint)(int)position.Y,
                    (uint)(int)position.Z);
                GridInstantMessage m = new GridInstantMessage(initiatingScene, UUID.Zero,
                "Region", agent.UUID,
                (byte)InstantMessageDialog.GodLikeRequestTeleport, false,
                "", gotoLocation, false, new Vector3(127, 0, 0),
                new Byte[0]);
                im.SendInstantMessage(m, delegate(bool success)
                {
                    //m_log.DebugFormat("[ENTITY TRANSFER MODULE]: Client Initiating Teleport sending IM success = {0}", success);
                });

            }
        }

        protected void InformClientToInitiateTeleportToLocationCompleted(IAsyncResult iar)
        {
            InformClientToInitateTeleportToLocationDelegate icon =
                (InformClientToInitateTeleportToLocationDelegate)iar.AsyncState;
            icon.EndInvoke(iar);
        }

        public delegate ScenePresence CrossAgentToNewRegionDelegate(ScenePresence agent, Vector3 pos, uint neighbourx, uint neighboury, bool isFlying);

        /// <summary>
        /// This Closes child agents on neighboring regions
        /// Calls an asynchronous method to do so..  so it doesn't lag the sim.
        /// </summary>
        protected ScenePresence CrossAgentToNewRegionAsync(ScenePresence agent, Vector3 pos, uint neighbourx, uint neighboury, bool isFlying)
        {
            m_log.DebugFormat("[ENTITY TRANSFER MODULE]: Crossing agent {0} {1} to {2}-{3}", agent.Firstname, agent.Lastname, neighbourx, neighboury);

            Scene m_scene = agent.Scene;
            ulong neighbourHandle = Utils.UIntsToLong((uint)(neighbourx * Constants.RegionSize), (uint)(neighboury * Constants.RegionSize));

            int x = (int)(neighbourx * Constants.RegionSize), y = (int)(neighboury * Constants.RegionSize);
            GridRegion neighbourRegion = m_scene.GridService.GetRegionByPosition(m_scene.RegionInfo.ScopeID, (int)x, (int)y);

            if (neighbourRegion != null && agent.ValidateAttachments())
            {
                pos = pos + (agent.Velocity);

                SetInTransit(agent.UUID);
                AgentData cAgent = new AgentData();
                agent.CopyTo(cAgent);
                cAgent.Position = pos;
                if (isFlying)
                    cAgent.ControlFlags |= (uint)AgentManager.ControlFlags.AGENT_CONTROL_FLY;
                cAgent.CallbackURI = "http://" + m_scene.RegionInfo.ExternalHostName + ":" + m_scene.RegionInfo.HttpPort +
                    "/agent/" + agent.UUID.ToString() + "/" + m_scene.RegionInfo.RegionID.ToString() + "/release/";

                if (!m_scene.SimulationService.UpdateAgent(neighbourRegion, cAgent))
                {
                    // region doesn't take it
                    ResetFromTransit(agent.UUID);
                    return agent;
                }

				agent.ControllingClient.RequestClientInfo();

                string agentcaps;
                ICapabilitiesModule module = m_scene.RequestModuleInterface<ICapabilitiesModule>();
                Dictionary<ulong, string> seeds = new Dictionary<ulong, string>();
                //Get children seeds from the CAPS module
                if (module != null)
                {
                    seeds = module.GetChildrenSeeds(agent.UUID);
                }
                if (!seeds.TryGetValue(neighbourRegion.RegionHandle, out agentcaps))
                {
                    m_log.ErrorFormat("[ENTITY TRANSFER MODULE]: No ENTITY TRANSFER MODULE information for region handle {0}, exiting CrossToNewRegion.",
                                     neighbourRegion.RegionHandle);
                    return agent;
                }
                // TODO Should construct this behind a method
                string capsPath =
                    "http://" + neighbourRegion.ExternalHostName + ":" + neighbourRegion.HttpPort
                     + "/CAPS/" + agentcaps /*circuitdata.CapsPath*/ + "0000/";

                m_log.DebugFormat("[ENTITY TRANSFER MODULE]: Sending new CAPS seed url {0} to client {1}", capsPath, agent.UUID);

                IEventQueueService eq = agent.Scene.RequestModuleInterface<IEventQueueService>();
                if (eq != null)
                {
                    eq.CrossRegion(neighbourHandle, pos, agent.Velocity, neighbourRegion.ExternalEndPoint,
                                   capsPath, agent.UUID, agent.ControllingClient.SessionId, agent.Scene.RegionInfo.RegionHandle);
                }
                else
                {
                    agent.ControllingClient.CrossRegion(neighbourHandle, pos, agent.Velocity, neighbourRegion.ExternalEndPoint,
                                                capsPath);
                }

                if (!WaitForCallback(agent.UUID))
                {
                    m_log.Debug("[ENTITY TRANSFER MODULE]: Callback never came in crossing agent");
                    ResetFromTransit(agent.UUID);

                    // Yikes! We should just have a ref to scene here.
                    //agent.Scene.InformClientOfNeighbours(agent);
                    EnableChildAgents(agent);

                    return agent;
                }

                // Next, let's close the child agent connections that are too far away.
                INeighborService neighborService = agent.Scene.RequestModuleInterface<INeighborService>();
                if (neighborService != null)
                    neighborService.CloseNeighborAgents(neighbourx, neighboury, agent.UUID, agent.Scene.RegionInfo.RegionID);

                agent.MakeChildAgent();
                // now we have a child agent in this region. Request all interesting data about other (root) agents
                agent.SendOtherAgentsAvatarDataToMe();
                agent.SendOtherAgentsAppearanceToMe();

                CrossAttachmentsIntoNewRegion(neighbourRegion, agent);
            }

            //m_log.Debug("AFTER CROSS");
            //Scene.DumpChildrenSeeds(UUID);
            //DumpKnownRegions();
            return agent;
        }

        private void KillAttachments(ScenePresence agent)
        {
            foreach (SceneObjectGroup grp in agent.Attachments)
            {
                //Kill in all clients as it will be readded in the other region
                KillEntities(agent.Scene, grp.ChildrenEntities().ToArray());
                //Now remove it from the Scene so that it will not come back
                agent.Scene.SceneGraph.DeleteEntity(grp);
                //And from storage as well
                IBackupModule backup = agent.Scene.RequestModuleInterface<IBackupModule>();
                if(backup != null)
                    backup.DeleteFromStorage(grp.UUID);
            }
        }

        /// <summary>
        /// This Closes child agents on neighboring regions
        /// Calls an asynchronous method to do so..  so it doesn't lag the sim.
        /// </summary>
        protected ScenePresence CrossAgentSittingToNewRegionAsync(ScenePresence agent, GridRegion neighbourRegion, SceneObjectGroup grp)
        {
            Scene m_scene = agent.Scene;
            
            if (agent.ValidateAttachments())
            {
                AgentData cAgent = new AgentData();
                agent.CopyTo(cAgent);
                cAgent.Position = grp.AbsolutePosition;
                    

                cAgent.CallbackURI = "http://" + m_scene.RegionInfo.ExternalHostName + ":" + m_scene.RegionInfo.HttpPort +
                    "/agent/" + agent.UUID.ToString() + "/" + m_scene.RegionInfo.RegionID.ToString() + "/release/";

                if (!m_scene.SimulationService.UpdateAgent(neighbourRegion, cAgent))
                {
                    // region doesn't take it
                    ResetFromTransit(agent.UUID);
                    return agent;
                }

                // Next, let's close the child agent connections that are too far away.
                INeighborService neighborService = agent.Scene.RequestModuleInterface<INeighborService>();
                if (neighborService != null)
                    neighborService.CloseNeighborAgents((uint)neighbourRegion.RegionLocX / 256, (uint)neighbourRegion.RegionLocY / 256, agent.UUID, agent.Scene.RegionInfo.RegionID);

                string agentcaps;
                ICapabilitiesModule module = m_scene.RequestModuleInterface<ICapabilitiesModule>();
                Dictionary<ulong, string> seeds = new Dictionary<ulong, string>();
                //Get children seeds from the CAPS module
                if (module != null)
                {
                    seeds = module.GetChildrenSeeds(agent.UUID);
                }
                if (!seeds.TryGetValue(neighbourRegion.RegionHandle, out agentcaps))
                {
                    m_log.ErrorFormat("[ENTITY TRANSFER MODULE]: No ENTITY TRANSFER MODULE information for region handle {0}, exiting CrossToNewRegion.",
                                     neighbourRegion.RegionHandle);
                    return agent;
                }
                // TODO Should construct this behind a method
                string capsPath =
                    "http://" + neighbourRegion.ExternalHostName + ":" + neighbourRegion.HttpPort
                     + "/CAPS/" + agentcaps /*circuitdata.CapsPath*/ + "0000/";

                m_log.DebugFormat("[ENTITY TRANSFER MODULE]: Sending new CAPS seed url {0} to client {1}", capsPath, agent.UUID);

                IEventQueueService eq = agent.Scene.RequestModuleInterface<IEventQueueService>();
                if (eq != null)
                {
                    eq.CrossRegion(neighbourRegion.RegionHandle, agent.AbsolutePosition, agent.Velocity, neighbourRegion.ExternalEndPoint,
                                   capsPath, agent.UUID, agent.ControllingClient.SessionId, agent.Scene.RegionInfo.RegionHandle);
                }
                else
                {
                    agent.ControllingClient.CrossRegion(neighbourRegion.RegionHandle, agent.AbsolutePosition, agent.Velocity, neighbourRegion.ExternalEndPoint,
                                                capsPath);
                }

                agent.MakeChildAgent();
                // now we have a child agent in this region. Request all interesting data about other (root) agents
                agent.SendOtherAgentsAvatarDataToMe();
                agent.SendOtherAgentsAppearanceToMe();

                CrossAttachmentsIntoNewRegion(neighbourRegion, agent);
            }
            return agent;
        }

        protected void CrossAgentToNewRegionCompleted(IAsyncResult iar)
        {
            CrossAgentToNewRegionDelegate icon = (CrossAgentToNewRegionDelegate)iar.AsyncState;
            ScenePresence agent = icon.EndInvoke(iar);

            // If the cross was successful, this agent is a child agent
            if (agent.IsChildAgent)
                agent.Reset();
            else // Not successful
                agent.RestoreInCurrentScene();

            // In any case
            agent.NotInTransit();

            //m_log.DebugFormat("[ENTITY TRANSFER MODULE]: Crossing agent {0} {1} completed.", agent.Firstname, agent.Lastname);
        }

        #endregion

        #region Enable Child Agent
        /// <summary>
        /// This informs a single neighboring region about agent "avatar".
        /// Calls an asynchronous method to do so..  so it doesn't lag the sim.
        /// </summary>
        public void EnableChildAgent(ScenePresence sp, GridRegion region)
        {
            m_log.DebugFormat("[ENTITY TRANSFER]: Enabling child agent in new neighour {0}", region.RegionName);

            AgentCircuitData currentAgentCircuit = sp.Scene.AuthenticateHandler.GetAgentCircuitData(sp.ControllingClient.CircuitCode);
            AgentCircuitData agent = sp.ControllingClient.RequestClientInfo();
            agent.BaseFolder = UUID.Zero;
            agent.InventoryFolder = UUID.Zero;
            agent.startpos = new Vector3(128, 128, 70);
            agent.child = true;
            agent.Appearance = sp.Appearance;
            agent.CapsPath = CapsUtil.GetRandomCapsObjectPath();

            ICapabilitiesModule module = sp.Scene.RequestModuleInterface<ICapabilitiesModule>();
            if (module != null)
                agent.ChildrenCapSeeds = new Dictionary<ulong, string>(module.GetChildrenSeeds(sp.UUID));
            //m_log.DebugFormat("[XXX] Seeds 1 {0}", agent.ChildrenCapSeeds.Count);

            if (!agent.ChildrenCapSeeds.ContainsKey(sp.Scene.RegionInfo.RegionHandle))
                agent.ChildrenCapSeeds.Add(sp.Scene.RegionInfo.RegionHandle, sp.ControllingClient.RequestClientInfo().CapsPath);
            //m_log.DebugFormat("[XXX] Seeds 2 {0}", agent.ChildrenCapSeeds.Count);

            //foreach (ulong h in agent.ChildrenCapSeeds.Keys)
            //    m_log.DebugFormat("[XXX] --> {0}", h);
            //m_log.DebugFormat("[XXX] Adding {0}", region.RegionHandle);
            agent.ChildrenCapSeeds.Add(region.RegionHandle, agent.CapsPath);

            if (module != null)
            {
                module.SetChildrenSeed(sp.UUID, agent.ChildrenCapSeeds);
            }

            if (currentAgentCircuit != null)
            {
                agent.ServiceURLs = currentAgentCircuit.ServiceURLs;
                agent.IPAddress = currentAgentCircuit.IPAddress;
                agent.Viewer = currentAgentCircuit.Viewer;
                agent.Channel = currentAgentCircuit.Channel;
                agent.Mac = currentAgentCircuit.Mac;
                agent.Id0 = currentAgentCircuit.Id0;
            }

            InformClientOfNeighbourDelegate d = InformClientOfNeighbourAsync;
            d.BeginInvoke(sp, agent, region, region.ExternalEndPoint, true,
                          InformClientOfNeighbourCompleted,
                          d);
        }
        #endregion

        #region Enable Child Agents

        private delegate void InformClientOfNeighbourDelegate(
            ScenePresence avatar, AgentCircuitData a, GridRegion reg, IPEndPoint endPoint, bool newAgent);

        /// <summary>
        /// This informs all neighboring regions about agent "avatar".
        /// Calls an asynchronous method to do so..  so it doesn't lag the sim.
        /// </summary>
        public virtual void EnableChildAgents(ScenePresence sp)
        {
            List<GridRegion> neighbours = new List<GridRegion>();
            RegionInfo m_regionInfo = sp.Scene.RegionInfo;

            if (m_regionInfo != null)
            {
                if (Util.VariableRegionSight && sp.DrawDistance != 0)
                {
                    int xMin = (int)(m_regionInfo.RegionLocX * (int)Constants.RegionSize) - (int)sp.DrawDistance;
                    int xMax = (int)(m_regionInfo.RegionLocX * (int)Constants.RegionSize) + (int)sp.DrawDistance;
                    int yMin = (int)(m_regionInfo.RegionLocX * (int)Constants.RegionSize) - (int)sp.DrawDistance;
                    int yMax = (int)(m_regionInfo.RegionLocX * (int)Constants.RegionSize) + (int)sp.DrawDistance;
                    
                    neighbours = sp.Scene.GridService.GetRegionRange(m_regionInfo.ScopeID,
                        xMin, xMax, yMin, yMax);
                }
                else
                {
                    INeighborService service = sp.Scene.RequestModuleInterface<INeighborService>();
                    if (service != null)
                    {
                        neighbours = service.GetNeighbors(sp.Scene.RegionInfo);
                    }
                }
            }
            else
            {
                m_log.Debug("[ENTITY TRANSFER MODULE]: m_regionInfo was null in EnableChildAgents, is this a NPC?");
            }
            
            /// We need to find the difference between the new regions where there are no child agents
            /// and the regions where there are already child agents. We only send notification to the former.
            List<ulong> neighbourHandles = NeighbourHandles(sp.Scene, neighbours); // on this region
            neighbourHandles.Add(sp.Scene.RegionInfo.RegionHandle);  // add this region too
            List<ulong> previousRegionNeighbourHandles;

            ICapabilitiesModule module = sp.Scene.RequestModuleInterface<ICapabilitiesModule>();
            if (module != null)
            {
                previousRegionNeighbourHandles =
                    new List<ulong>(module.GetChildrenSeeds(sp.UUID).Keys);
            }
            else
            {
                previousRegionNeighbourHandles = new List<ulong>();
            }

            List<ulong> newRegions = NewNeighbours(neighbourHandles, previousRegionNeighbourHandles);
            List<ulong> oldRegions = OldNeighbours(neighbourHandles, previousRegionNeighbourHandles);

            //Dump("Current Neighbors", neighbourHandles);
            //Dump("Previous Neighbours", previousRegionNeighbourHandles);
            //Dump("New Neighbours", newRegions);
            //Dump("Old Neighbours", oldRegions);

            /// Update the scene presence's known regions here on this region
            sp.DropOldNeighbors(oldRegions);

            /// Collect as many seeds as possible
            Dictionary<ulong, string> seeds;
            if (module != null)
                seeds
                    = new Dictionary<ulong, string>(module.GetChildrenSeeds(sp.UUID));
            else
                seeds = new Dictionary<ulong, string>();

            //m_log.Debug(" !!! No. of seeds: " + seeds.Count);
            if (!seeds.ContainsKey(sp.Scene.RegionInfo.RegionHandle))
                seeds.Add(sp.Scene.RegionInfo.RegionHandle, sp.ControllingClient.RequestClientInfo().CapsPath);

            /// Create the necessary child agents
            List<AgentCircuitData> cagents = new List<AgentCircuitData>();
            foreach (GridRegion neighbour in neighbours)
            {
                if (neighbour.RegionHandle != sp.Scene.RegionInfo.RegionHandle)
                {

                    AgentCircuitData currentAgentCircuit = sp.Scene.AuthenticateHandler.GetAgentCircuitData(sp.ControllingClient.CircuitCode);
                    AgentCircuitData agent = sp.ControllingClient.RequestClientInfo();
                    agent.BaseFolder = UUID.Zero;
                    agent.InventoryFolder = UUID.Zero;
                    agent.startpos = new Vector3(128, 128, 70);
                    agent.child = true;
                    agent.Appearance = sp.Appearance;
                    if (currentAgentCircuit != null)
                    {
                        agent.ServiceURLs = currentAgentCircuit.ServiceURLs;
                        agent.IPAddress = currentAgentCircuit.IPAddress;
                        agent.Viewer = currentAgentCircuit.Viewer;
                        agent.Channel = currentAgentCircuit.Channel;
                        agent.Mac = currentAgentCircuit.Mac;
                        agent.Id0 = currentAgentCircuit.Id0;
                    }

                    if (newRegions.Contains(neighbour.RegionHandle))
                    {
                        agent.CapsPath = CapsUtil.GetRandomCapsObjectPath();
                        if(!seeds.ContainsKey(neighbour.RegionHandle))
                            seeds.Add(neighbour.RegionHandle, agent.CapsPath);
                    }
                    else if (module != null)
                        agent.CapsPath = module.GetChildSeed(sp.UUID, neighbour.RegionHandle);

                    cagents.Add(agent);
                }
                else
                    cagents.Add(new AgentCircuitData());
            }

            /// Update all child agent with everyone's seeds
            foreach (AgentCircuitData a in cagents)
            {
                a.ChildrenCapSeeds = new Dictionary<ulong, string>(seeds);
            }

            if (module != null)
            {
                module.SetChildrenSeed(sp.UUID, seeds);
            }
            //avatar.Scene.DumpChildrenSeeds(avatar.UUID);
            //avatar.DumpKnownRegions();

            bool newAgent = false;
            int count = 0;
            foreach (GridRegion neighbour in neighbours)
            {
                // Don't do it if there's already an agent in that region
                if (newRegions.Contains(neighbour.RegionHandle))
                    newAgent = true;
                else
                    newAgent = false;
                //m_log.WarnFormat("--> Going to send child agent to {0}, new agent {1}", neighbour.RegionName, newAgent);
                
                if (neighbour.RegionHandle != sp.Scene.RegionInfo.RegionHandle)
                {
                    InformClientOfNeighbourDelegate d = InformClientOfNeighbourAsync;
                    try
                    {
                        d.BeginInvoke(sp, cagents[count], neighbour, neighbour.ExternalEndPoint, newAgent,
                                      InformClientOfNeighbourCompleted,
                                      d);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        m_log.ErrorFormat(
                           "[ENTITY TRANSFER MODULE]: Neighbour Regions response included the current region in the neighbor list.  The following region will not display to the client: {0} for region {1} ({2}, {3}).",
                           neighbour.ExternalHostName,
                           neighbour.RegionHandle,
                           neighbour.RegionLocX,
                           neighbour.RegionLocY);
                    }
                    catch (Exception e)
                    {
                        m_log.ErrorFormat(
                            "[ENTITY TRANSFER MODULE]: Could not resolve external hostname {0} for region {1} ({2}, {3}).  {4}",
                            neighbour.ExternalHostName,
                            neighbour.RegionHandle,
                            neighbour.RegionLocX,
                            neighbour.RegionLocY,
                            e);

                        // FIXME: Okay, even though we've failed, we're still going to throw the exception on,
                        // since I don't know what will happen if we just let the client continue

                        // XXX: Well, decided to swallow the exception instead for now.  Let us see how that goes.
                        // throw e;

                    }
                }
                count++;
            }
        }

        private void InformClientOfNeighbourCompleted(IAsyncResult iar)
        {
            InformClientOfNeighbourDelegate icon = (InformClientOfNeighbourDelegate)iar.AsyncState;
            icon.EndInvoke(iar);
            //m_log.WarnFormat(" --> InformClientOfNeighbourCompleted");
        }

        /// <summary>
        /// Async component for informing client of which neighbours exist
        /// </summary>
        /// <remarks>
        /// This needs to run asynchronously, as a network timeout may block the thread for a long while
        /// </remarks>
        /// <param name="remoteClient"></param>
        /// <param name="a"></param>
        /// <param name="regionHandle"></param>
        /// <param name="endPoint"></param>
        private void InformClientOfNeighbourAsync(ScenePresence sp, AgentCircuitData a, GridRegion reg,
                                                  IPEndPoint endPoint, bool newAgent)
        {
            // Let's wait just a little to give time to originating regions to catch up with closing child agents
            // after a cross here
            Thread.Sleep(500);

            Scene m_scene = sp.Scene;

            uint x, y;
            Utils.LongToUInts(reg.RegionHandle, out x, out y);
            x = x / Constants.RegionSize;
            y = y / Constants.RegionSize;
            m_log.Info("[ENTITY TRANSFER MODULE]: Starting to inform client about neighbour " + x + ", " + y + "(" + endPoint.ToString() + ")");

            string capsPath = "http://" + reg.ExternalHostName + ":" + reg.HttpPort
                  + "/CAPS/" + a.CapsPath + "0000/";

            string reason = String.Empty;


            bool regionAccepted = m_scene.SimulationService.CreateAgent(reg, a, (uint)TeleportFlags.Default, out reason); 

            if (regionAccepted && newAgent)
            {
                IEventQueueService eq = sp.Scene.RequestModuleInterface<IEventQueueService>();
                if (eq != null)
                {
                    #region IP Translation for NAT
                    IClientIPEndpoint ipepClient;
                    if (sp.ClientView.TryGet(out ipepClient))
                    {
                        endPoint.Address = NetworkUtil.GetIPFor(ipepClient.EndPoint, endPoint.Address);
                    }
                    #endregion

                    //m_log.DebugFormat("[ENTITY TRANSFER MODULE]: {0} is sending {1} EnableSimulator for neighbor region {2} @ {3} " +
                    //    "and EstablishAgentCommunication with seed cap {4}",
                    //    m_scene.RegionInfo.RegionName, sp.Name, reg.RegionName, reg.RegionHandle, capsPath);

                    eq.EnableSimulator(reg.RegionHandle, endPoint, sp.UUID, sp.Scene.RegionInfo.RegionHandle);
                    eq.EstablishAgentCommunication(sp.UUID, reg.RegionHandle, endPoint, capsPath, sp.Scene.RegionInfo.RegionHandle);
                }
                else
                {
                    sp.ControllingClient.InformClientOfNeighbor(reg.RegionHandle, endPoint);
                }

                m_log.Info("[ENTITY TRANSFER MODULE]: Completed inform client about neighbour " + endPoint.ToString());

            }

        }

        private List<ulong> NewNeighbours(List<ulong> currentNeighbours, List<ulong> previousNeighbours)
        {
            return currentNeighbours.FindAll(delegate(ulong handle) { return !previousNeighbours.Contains(handle); });
        }

        private List<ulong> OldNeighbours(List<ulong> currentNeighbours, List<ulong> previousNeighbours)
        {
            return previousNeighbours.FindAll(delegate(ulong handle) { return !currentNeighbours.Contains(handle); });
        }

        private List<ulong> NeighbourHandles(Scene currentScene, List<GridRegion> neighbours)
        {
            List<ulong> handles = new List<ulong>();
            foreach (GridRegion reg in neighbours)
            {
                int x = (int)currentScene.RegionInfo.RegionLocX - reg.RegionLocX;
                int y = (int)currentScene.RegionInfo.RegionLocY - reg.RegionLocY;
                if ((x < 2 && x > -2) && (x < 2 && x > -2)) //We only check in the nearby regions for now
                {
                    if (!currentScene.DirectionsToBlockChildAgents[x, y]) //Not blocked, so false
                        handles.Add(reg.RegionHandle);
                }
                else
                    handles.Add(reg.RegionHandle);
            }
            return handles;
        }

        #endregion

        #region Agent Arrived

        public void AgentArrivedAtDestination(UUID id)
        {
            //m_log.Debug(" >>> ReleaseAgent called <<< ");
            ResetFromTransit(id);
        }

        #endregion

        #region Object Transfers

        /// <summary>
        /// Move the given scene object into a new region depending on which region its absolute position has moved
        /// into.
        ///
        /// This method locates the new region handle and offsets the prim position for the new region
        /// </summary>
        /// <param name="attemptedPosition">the attempted out of region position of the scene object</param>
        /// <param name="grp">the scene object that we're crossing</param>
        public void CrossGroupToNewRegion(SceneObjectGroup grp, Vector3 attemptedPosition)
        {
            if (grp == null)
                return;
            if (grp.IsDeleted)
                return;

            Scene scene = grp.Scene;
            if (scene == null)
                return;
            if (grp.RootPart.DIE_AT_EDGE)
            {
                // We remove the object here
                try
                {
                    scene.DeleteSceneObject(grp, true);
                }
                catch (Exception)
                {
                    m_log.Warn("[DATABASE]: exception when trying to remove the prim that crossed the border.");
                }
                return;
            }

            if (grp.RootPart.RETURN_AT_EDGE)
            {
                // We remove the object here
                try
                {
                    List<SceneObjectGroup> objects = new List<SceneObjectGroup>() { grp };
                    scene.returnObjects(objects.ToArray(), UUID.Zero);
                }
                catch (Exception)
                {
                    m_log.Warn("[SCENE]: exception when trying to return the prim that crossed the border.");
                }
                return;
            }

            int thisx = (int)scene.RegionInfo.RegionLocX;
            int thisy = (int)scene.RegionInfo.RegionLocY;
            Vector3 EastCross = new Vector3(0.1f, 0, 0);
            Vector3 WestCross = new Vector3(-0.1f, 0, 0);
            Vector3 NorthCross = new Vector3(0, 0.1f, 0);
            Vector3 SouthCross = new Vector3(0, -0.1f, 0);


            // use this if no borders were crossed!
            ulong newRegionHandle
                        = Util.UIntsToLong((uint)((thisx) * Constants.RegionSize),
                                           (uint)((thisy) * Constants.RegionSize));

            Vector3 pos = attemptedPosition;

            //TODO: Fix up group transfer as its probably broken

            // Offset the positions for the new region across the border
            Vector3 oldGroupPosition = grp.RootPart.GroupPosition;
            grp.OffsetForNewRegion(pos);

            // If we fail to cross the border, then reset the position of the scene object on that border.
            uint x = 0, y = 0;
            Utils.LongToUInts(newRegionHandle, out x, out y);
            GridRegion destination = scene.GridService.GetRegionByPosition(UUID.Zero, (int)x, (int)y);
            if (destination != null && !CrossPrimGroupIntoNewRegion(destination, grp))
            {
                grp.OffsetForNewRegion(oldGroupPosition);
                grp.ScheduleGroupUpdate(PrimUpdateFlags.FullUpdate);
            }
        }

        /// <summary>
        /// Move the given scene object into a new region
        /// </summary>
        /// <param name="newRegionHandle"></param>
        /// <param name="grp">Scene Object Group that we're crossing</param>
        /// <returns>
        /// true if the crossing itself was successful, false on failure
        /// </returns>
        protected bool CrossAttachmentIntoNewRegion(GridRegion destination, SceneObjectGroup grp, UUID userID, UUID ItemID)
        {
            bool successYN = false;
            grp.RootPart.ClearUpdateScheduleOnce();
            if (destination != null)
            {
                if (grp.Scene != null && grp.Scene.SimulationService != null)
                    successYN = grp.Scene.SimulationService.CreateObject(destination, userID, ItemID);

                if (successYN)
                {
                    // We remove the object here
                    try
                    {
                        grp.Scene.DeleteSceneObject(grp, false);
                    }
                    catch (Exception e)
                    {
                        m_log.ErrorFormat(
                            "[ENTITY TRANSFER MODULE]: Exception deleting the old object left behind on a border crossing for {0}, {1}",
                            grp, e);
                    }
                }
                else
                {
                    if (!grp.IsDeleted)
                    {
                        if (grp.RootPart.PhysActor != null)
                        {
                            grp.RootPart.PhysActor.CrossingFailure();
                        }
                    }

                    m_log.ErrorFormat("[ENTITY TRANSFER MODULE]: Prim crossing failed for {0}", grp);
                }
            }
            else
            {
                m_log.Error("[ENTITY TRANSFER MODULE]: destination was unexpectedly null in Scene.CrossPrimGroupIntoNewRegion()");
            }

            return successYN;
        }

        /// <summary>
        /// Move the given scene object into a new region
        /// </summary>
        /// <param name="newRegionHandle"></param>
        /// <param name="grp">Scene Object Group that we're crossing</param>
        /// <returns>
        /// true if the crossing itself was successful, false on failure
        /// </returns>
        protected bool CrossPrimGroupIntoNewRegion(GridRegion destination, SceneObjectGroup grp)
        {
            bool successYN = false;
            grp.RootPart.ClearUpdateScheduleOnce();
            if (destination != null)
            {
                if (grp.RootPart.SitTargetAvatar.Count != 0)
                {
                    lock (grp.RootPart.SitTargetAvatar)
                    {
                        foreach (UUID avID in grp.RootPart.SitTargetAvatar)
                        {
                            ScenePresence SP = grp.Scene.GetScenePresence(avID);
                            CrossAgentSittingToNewRegionAsync(SP, destination, grp);
                        }
                    }
                }

                if (grp.Scene != null && grp.Scene.SimulationService != null)
                    successYN = grp.Scene.SimulationService.CreateObject(destination, grp);

                if (successYN)
                {
                    // We remove the object here
                    try
                    {
                        foreach (SceneObjectPart part in grp.ChildrenList)
                        {
                            lock (part.SitTargetAvatar)
                            {
                                part.SitTargetAvatar.Clear();
                            }
                        }
                        return grp.Scene.DeleteSceneObject(grp, false);
                    }
                    catch (Exception e)
                    {
                        m_log.ErrorFormat(
                            "[ENTITY TRANSFER MODULE]: Exception deleting the old object left behind on a border crossing for {0}, {1}",
                            grp, e);
                    }
                }
                else
                {
                    if (!grp.IsDeleted)
                    {
                        if (grp.RootPart.PhysActor != null)
                        {
                            grp.RootPart.PhysActor.CrossingFailure();
                        }
                    }

                    m_log.ErrorFormat("[ENTITY TRANSFER MODULE]: Prim crossing failed for {0}", grp);
                }
            }
            else
            {
                m_log.Error("[ENTITY TRANSFER MODULE]: destination was unexpectedly null in Scene.CrossPrimGroupIntoNewRegion()");
            }

            return successYN;
        }

        protected bool CrossAttachmentsIntoNewRegion(GridRegion destination, ScenePresence sp)
        {
            List<SceneObjectGroup> m_attachments = sp.Attachments;
            lock (m_attachments)
            {
                //Kill the groups here, otherwise they will become ghost attachments 
                //  and stay in the sim, they'll get readded below into the new sim
                KillAttachments(sp);

                foreach (SceneObjectGroup gobj in m_attachments)
                {
                    // If the prim group is null then something must have happened to it!
                    if (gobj != null && gobj.RootPart != null)
                    {
                        // Set the parent localID to 0 so it transfers over properly.
                        gobj.RootPart.SetParentLocalId(0);
                        gobj.AbsolutePosition = gobj.RootPart.AttachedPos;
                        gobj.RootPart.IsAttachment = false;
                        //gobj.RootPart.LastOwnerID = gobj.GetFromAssetID();
                        m_log.InfoFormat("[ENTITY TRANSFER MODULE]: Sending attachment {0} to region {1}", gobj.UUID, destination.RegionName);
                        CrossAttachmentIntoNewRegion(destination, gobj, sp.UUID, gobj.RootPart.FromItemID);
                    }
                }
                m_attachments.Clear();

                return true;
            }
        }

        #endregion

        #region Incoming Object Transfers

        /// <summary>
        /// Attachment rezzing
        /// </summary>
        /// <param name="userID">Agent Unique ID</param>
        /// <param name="itemID">Inventory Item ID to rez</param>
        /// <returns>False</returns>
        public virtual bool IncomingCreateObject(UUID regionID, UUID userID, UUID itemID)
        {
            //m_log.DebugFormat(" >>> IncomingCreateObject(userID, itemID) <<< {0} {1}", userID, itemID);
            Scene scene = GetScene(regionID);
            if (scene == null)
                return false;
            ScenePresence sp = scene.GetScenePresence(userID);
            IAttachmentsModule attachMod = scene.RequestModuleInterface<IAttachmentsModule>();
            if (sp != null && attachMod != null)
            {
                m_log.DebugFormat(
                        "[EntityTransferModule]: Received attachment via new attachment method {0} for agent {1}", itemID, sp.Name);
                int attPt = sp.Appearance.GetAttachpoint(itemID);
                attachMod.RezSingleAttachmentFromInventory(sp.ControllingClient, itemID, attPt);
            }

            return false;
        }

        /// <summary>
        /// Called when objects or attachments cross the border, or teleport, between regions.
        /// </summary>
        /// <param name="sog"></param>
        /// <returns></returns>
        public virtual bool IncomingCreateObject(UUID regionID, ISceneObject sog)
        {
            Scene scene = GetScene(regionID);
            if (scene == null)
                return false;
            
            //m_log.Debug(" >>> IncomingCreateObject(sog) <<< " + ((SceneObjectGroup)sog).AbsolutePosition + " deleted? " + ((SceneObjectGroup)sog).IsDeleted);
            SceneObjectGroup newObject;
            try
            {
                newObject = (SceneObjectGroup)sog;
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[EntityTransferModule]: Problem casting object: {0}", e.Message);
                return false;
            }

            if (!AddSceneObject(scene, newObject))
            {
                m_log.DebugFormat("[EntityTransferModule]: Problem adding scene object {0} in {1} ", sog.UUID, scene.RegionInfo.RegionName);
                return false;
            }

            newObject.RootPart.ParentGroup.CreateScriptInstances(0, false, scene.DefaultScriptEngine, 1, UUID.Zero);
            newObject.RootPart.ParentGroup.ResumeScripts();

            // Do this as late as possible so that listeners have full access to the incoming object
            scene.EventManager.TriggerOnIncomingSceneObject(newObject);

            if (newObject.RootPart.SitTargetAvatar.Count != 0)
            {
                lock (newObject.RootPart.SitTargetAvatar)
                {
                    foreach (UUID avID in newObject.RootPart.SitTargetAvatar)
                    {
                        ScenePresence SP = scene.GetScenePresence(avID);
                        while (SP == null)
                        {
                            Thread.Sleep(20);
                        }
                        SP.AbsolutePosition = newObject.AbsolutePosition;
                        SP.CrossSittingAgent(SP.ControllingClient, newObject.RootPart.UUID);
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Adds a Scene Object group to the Scene.
        /// Verifies that the creator of the object is not banned from the simulator.
        /// Checks if the item is an Attachment
        /// </summary>
        /// <param name="sceneObject"></param>
        /// <returns>True if the SceneObjectGroup was added, False if it was not</returns>
        public bool AddSceneObject(Scene scene, SceneObjectGroup sceneObject)
        {
            // If the user is banned, we won't let any of their objects
            // enter. Period.
            //
            if (scene.RegionInfo.EstateSettings.IsBanned(sceneObject.OwnerID))
            {
                m_log.Info("[EntityTransferModule]: Denied prim crossing for banned avatar");

                return false;
            }

            if (sceneObject.IsAttachmentCheckFull()) // Attachment
            {
                sceneObject.RootPart.AddFlag(PrimFlags.TemporaryOnRez);
                sceneObject.RootPart.AddFlag(PrimFlags.Phantom);
                scene.SceneGraph.AddPrimToScene(sceneObject);

                // Fix up attachment Parent Local ID
                ScenePresence sp = scene.GetScenePresence(sceneObject.OwnerID);

                if (sp != null)
                {
                    m_log.DebugFormat(
                        "[EntityTransferModule]: Received attachment {0}, inworld asset id {1}", sceneObject.GetFromItemID(), sceneObject.UUID);
                    m_log.DebugFormat(
                        "[EntityTransferModule]: Attach to avatar {0} at position {1}", sp.UUID, sceneObject.AbsolutePosition);

                    IAttachmentsModule attachModule = scene.RequestModuleInterface<IAttachmentsModule>();
                    if (attachModule != null)
                        attachModule.AttachObject(sp.ControllingClient, sceneObject.LocalId, 0, false);

                    sceneObject.RootPart.RemFlag(PrimFlags.TemporaryOnRez);
                }
                else
                {
                    sceneObject.RootPart.RemFlag(PrimFlags.TemporaryOnRez);
                    sceneObject.RootPart.AddFlag(PrimFlags.TemporaryOnRez);
                }
            }
            else
            {
                if (!scene.Permissions.CanObjectEntry(sceneObject.UUID,
                        true, sceneObject.AbsolutePosition))
                {
                    // Deny non attachments based on parcel settings
                    //
                    m_log.Info("[EntityTransferModule]: Denied prim crossing " +
                            "because of parcel settings");

                    scene.DeleteSceneObject(sceneObject, true);

                    return false;
                }
                scene.SceneGraph.AddPrimToScene(sceneObject);
            }
            sceneObject.ScheduleGroupUpdate(PrimUpdateFlags.FullUpdate);

            return true;
        }

        #endregion

        #region Misc

        protected bool WaitForCallback(UUID id)
        {
            int count = 200;
            while (m_agentsInTransit.Contains(id) && count-- > 0)
            {
                //m_log.Debug("  >>> Waiting... " + count);
                Thread.Sleep(100);
            }

            if (count > 0)
                return true;
            else
                return false;
        }

        protected void SetInTransit(UUID id)
        {
            lock (m_agentsInTransit)
            {
                if (!m_agentsInTransit.Contains(id))
                    m_agentsInTransit.Add(id);
            }
        }

        protected bool ResetFromTransit(UUID id)
        {
            lock (m_agentsInTransit)
            {
                if (m_agentsInTransit.Contains(id))
                {
                    m_agentsInTransit.Remove(id);
                    return true;
                }
            }
            return false;
        }

        public void CancelTeleport(UUID AgentID)
        {
            m_cancelingAgents.Add(AgentID);
        }

        /// <summary>
        /// This 'can' return null, so be careful
        /// </summary>
        /// <param name="RegionID"></param>
        /// <returns></returns>
        public Scene GetScene(UUID RegionID)
        {
            foreach (Scene scene in m_scenes)
            {
                if (scene.RegionInfo.RegionID == RegionID)
                    return scene;
            }
            return null;
        }

        /// <summary>
        /// This is pretty much guaranteed to return a Scene
        ///   as it will return the first scene if it cannot find the scene
        /// </summary>
        /// <param name="RegionID"></param>
        /// <returns></returns>
        public Scene TryGetScene(UUID RegionID)
        {
            foreach (Scene scene in m_scenes)
            {
                if (scene.RegionInfo.RegionID == RegionID)
                    return scene;
            }
            return m_scenes[0];
        }

        #endregion
    }
}
