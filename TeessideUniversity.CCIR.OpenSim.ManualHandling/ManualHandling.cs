/*
 * Copyright (c) Contributors, Teesside University Centre for Construction Innovation and Research
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
using System.Xml;
using System.Reflection;
using System.Collections.Generic;

using log4net;
using Nini.Config;
using Mono.Addins;
using OpenMetaverse;

using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;

using LSL_Float = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLFloat;
using LSL_Integer = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLInteger;
using LSL_Key = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_List = OpenSim.Region.ScriptEngine.Shared.LSL_Types.list;
using LSL_Rotation = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Quaternion;
using LSL_String = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_Vector = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Vector3;

[assembly: Addin("ManualHandling", "0.1")]
[assembly: AddinDependency("OpenSim", "0.7.5")]

namespace TeessideUniversity.CCIR.OpenSim
{
    enum foo
    {
        bar,
        baz,
        bat
    }

    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "ManualHandling")]
    class ManualHandling : INonSharedRegionModule
    {
        const int bag = (int)foo.bar;

        #region logging

        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        #endregion

        private Scene m_scene;
        private IScriptModuleComms m_scriptModuleComms;

        bool m_enabled = false;

        #region INonSharedRegionModule

        public string Name
        {
            get { return "ManualHandling"; }
        }

        public void Initialise(IConfigSource config)
        {
            IConfig conf = config.Configs["TSU.CCIR.OSSL"];

            m_enabled = (conf != null && conf.GetBoolean("Enabled", false));
            if (m_enabled)
            {
                m_enabled = (conf != null && conf.GetBoolean(Name, false));
            }

            m_log.Info("[TSU.CCIR." + Name + "]: " + (m_enabled ? "Enabled" : "Disabled"));
        }

        public void AddRegion(Scene scene)
        {
        }

        public void RemoveRegion(Scene scene)
        {
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_enabled)
                return;

            m_scene = scene;

            m_scriptModuleComms = scene.RequestModuleInterface<IScriptModuleComms>();

            if (m_scriptModuleComms == null)
            {
                m_log.Error("IScriptModuleComms could not be found, cannot add script functions");
                return;
            }

            m_scriptModuleComms.RegisterScriptInvocation(this, new string[]{
                "tsuccirSetLoadBearingLimit",
                "tsuccirGetLoadBearingLimit"
            });

            m_scene.EventManager.OnRemovePresence += EventManager_OnRemovePresence;
            m_scene.EventManager.OnNewPresence += EventManager_OnNewPresence;
        }

        public void Close()
        {
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        #endregion

        #region OSSL

        private void ScriptError(SceneObjectPart origin, string msg)
        {
            ScriptError(origin.UUID, origin.Name, origin.AbsolutePosition, msg);
        }

        private void ScriptError(UUID origin, string originName, Vector3 pos, string msg)
        {
            m_scene.SimChat(msg, ChatTypeEnum.DebugChannel, pos, originName, origin, false);
        }

        #region Event Handlers

        void EventManager_OnRemovePresence(UUID agentId)
        {
            m_loadBearingLimit.Remove(agentId);
        }

        void EventManager_OnNewPresence(ScenePresence presence)
        {
            if(presence.PresenceType == PresenceType.User)
            {
                m_loadBearingLimit[presence.UUID] = 0.0f;
            }
        }

        #endregion

        #region weight limits

        private Dictionary<UUID, float> m_loadBearingLimit = new Dictionary<UUID, float>();

        public int tsuccirSetLoadBearingLimit(UUID host, UUID script, UUID agent, float limit)
        {
            SceneObjectPart hostPart = null;
            if (!m_scene.TryGetSceneObjectPart(host, out hostPart))
            {
                ScriptError(host, "unknown", new Vector3(m_scene.Center), "Could not set load bearing limit, originating prim could not be found.");
                return 0;
            }

            TaskInventoryItem scriptItem = null;
            if (!hostPart.TaskInventory.TryGetValue(script, out scriptItem))
            {
                ScriptError(hostPart, "Could not set load bearing limit, script not found.");
                return 0;
            }

            if (scriptItem.OwnerID != m_scene.RegionInfo.EstateSettings.EstateOwner)
            {
                LandData parcel = m_scene.GetLandData(hostPart.AbsolutePosition);
                if (parcel == null)
                {
                    ScriptError(hostPart, "Could not set load bearing limit, parcel could not be found.");
                    return 0;
                }
                else if (scriptItem.OwnerID != parcel.OwnerID)
                {
                    ScriptError(hostPart, "Could not set load bearing limit, script owner does not match estate owner or parcel owner.");
                    return 0;
                }
            }

            ScenePresence agentPresence = null;
            if (!m_scene.TryGetScenePresence(agent, out agentPresence))
            {
                ScriptError(hostPart, "Could not set load bearing limit, agent not found.");
                return 0;
            }

            m_loadBearingLimit[agent] = limit;
            return 1;
        }

        public float tsuccirGetLoadBearingLimit(UUID host, UUID script, UUID agent)
        {
            if(m_loadBearingLimit.ContainsKey(agent))
                return m_loadBearingLimit[agent];
            else
                return 0;
        }

        #endregion

        #endregion
    }
}
