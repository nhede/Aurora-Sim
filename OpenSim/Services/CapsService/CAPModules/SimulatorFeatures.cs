/*
 * Copyright (c) Contributors, http://aurora-sim.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the Aurora-Sim Project nor the
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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Framework;
using OpenSim.Framework.Capabilities;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.CapsService
{
    public class SimulatorFeatures : ICapsServiceConnector
    {
        private IRegionClientCapsService m_service;

        private Hashtable SimulatorFeaturesCAP (Hashtable mDhttpMethod)
        {
            OSDMap data = new OSDMap ();
            data["MeshRezEnabled"] = true;
            data["MeshUploadEnabled"] = true;
            data["MeshXferEnabled"] = true;
            data["PhysicsMaterialsEnabled"] = true;


            OSDMap typesMap = new OSDMap ();

            typesMap["convex"] = true;
            typesMap["none"] = true;
            typesMap["prim"] = true;

            data["PhysicsShapeTypes"] = typesMap;



            //Data URLS need sent as well
            //Not yet...
            //data["DataUrls"] = m_service.Registry.RequestModuleInterface<IGridRegistrationService> ().GetUrlForRegisteringClient (m_service.AgentID + "|" + m_service.RegionHandle);

            //Send back data
            Hashtable responsedata = new Hashtable ();
            responsedata["int_response_code"] = 200; //501; //410; //404;
            responsedata["content_type"] = "text/plain";
            responsedata["keepalive"] = false;
            responsedata["str_response_string"] = OSDParser.SerializeLLSDXmlString(data);
            return responsedata;
        }

        public void RegisterCaps (IRegionClientCapsService service)
        {
            m_service = service;

            m_service.AddStreamHandler ("SimulatorFeatures", new RestHTTPHandler ("GET", m_service.CreateCAPS ("SimulatorFeatures", ""),
                                                      delegate (Hashtable m_dhttpMethod)
                                                      {
                                                          return SimulatorFeaturesCAP (m_dhttpMethod);
                                                      }));
        }

        public void DeregisterCaps ()
        {
            m_service.RemoveStreamHandler ("SimulatorFeatures", "GET");
        }

        public void EnteringRegion ()
        {
        }
    }
}
