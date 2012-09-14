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
[assembly: AddinDependency("TSU.CCIR.OpenSim.LSL", "0.1")]
[assembly: AddinDependency("TSU.CCIR.OpenSim.PrimDescendants", "0.1")]

namespace TeessideUniversity.CCIR.OpenSim
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "ManualHandling")]
    public class ManualHandling : INonSharedRegionModule
    {
        #region logging

        private static readonly ILog m_log = LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        #endregion

        private Scene m_scene;
        private IScriptModuleComms m_modComms;

        bool m_enabled = false;

        #region INonSharedRegionModule

        public string Name
        {
            get { return "ManualHandling"; }
        }

        public void Initialise(IConfigSource config)
        {
            IConfig conf = config.Configs["TSU.CCIR.OpenSim"];

            m_enabled = (conf != null && conf.GetBoolean("Enabled", false));
            if (m_enabled)
            {
                m_enabled = (conf != null && conf.GetBoolean(Name, false));
            }

            m_log.Info("[TSU.CCIR." + Name + "]: " +
                    (m_enabled ? "Enabled" : "Disabled"));
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

            m_modComms = scene.RequestModuleInterface<IScriptModuleComms>();

            if (m_modComms == null)
            {
                m_log.Error(
                        "IScriptModuleComms could not be found, cannot add" +
                        " script functions");
                return;
            }

            m_modComms.RegisterScriptInvocations(this);

            m_scene.EventManager.OnRemovePresence += OnRemovePresence;
            m_scene.EventManager.OnNewPresence += OnNewPresence;
            m_scene.EventManager.OnObjectBeingRemovedFromScene += OnObjectBeingRemovedFromScene;
            m_scene.EventManager.OnAttach += OnAttach;
        }

        public void Close()
        {
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        #endregion

        #region Event Handlers

        void OnRemovePresence(UUID agentId)
        {
            m_loadBearingLimits.Remove(agentId);
            m_occupiedAttachPoints.Remove(agentId);
        }

        void OnNewPresence(ScenePresence presence)
        {
            if (presence.PresenceType == PresenceType.User)
            {
                m_loadBearingLimits[presence.UUID] = 0.0f;
            }
        }

        void OnObjectBeingRemovedFromScene(SceneObjectGroup sog)
        {
            if (m_occupiedAttachPoints.ContainsKey(sog.OwnerID))
                m_occupiedAttachPoints[sog.OwnerID].Remove(sog.UUID);

            m_objectMass.Remove(sog.UUID);
        }

        void OnAttach(uint localID, UUID itemID, UUID avatarID)
        {
            if (avatarID == UUID.Zero)
            {
                foreach (KeyValuePair<UUID, Dictionary<UUID, List<int>>> kvp in
                        m_occupiedAttachPoints)
                {
                    kvp.Value.Remove(itemID);
                }
            }
        }

        #endregion

        #region OSSL

        private void ScriptError(SceneObjectPart origin, string msg)
        {
            ScriptError(origin.UUID, origin.Name, origin.AbsolutePosition, msg);
        }

        private void ScriptError(UUID origin, string originName, Vector3 pos,
                string msg)
        {
            m_scene.SimChat(msg, ChatTypeEnum.DebugChannel, pos, originName,
                    origin, false);
        }

        #region weight limits

        /// <summary>
        /// Stores the load bearing limit of each avatar
        /// </summary>
        private Dictionary<UUID, float> m_loadBearingLimits = new Dictionary<UUID, float>();

        /// <summary>
        /// Stores the custom mass of each object. Values are in Kilograms.
        /// </summary>
        private Dictionary<UUID, float> m_objectMass = new Dictionary<UUID, float>();

        /// <summary>
        /// Sets the load bearing limit for the specified avatar.
        /// </summary>
        /// <param name="host"></param>
        /// <param name="script"></param>
        /// <param name="agent"></param>
        /// <param name="limit"></param>
        /// <returns></returns>
        [ScriptInvocation]
        public int tsuccirSetLoadBearingLimit(UUID hostID, UUID script,
                string agent, float limit)
        {
            SceneObjectPart host = null;
            if (!m_scene.TryGetSceneObjectPart(hostID, out host))
            {
                ScriptError(hostID, "unknown", new Vector3(m_scene.Center),
                        "Could not set load bearing limit, originating prim" +
                        " could not be found.");
                return 0;
            }

            TaskInventoryItem scriptItem = null;
            if (!host.TaskInventory.TryGetValue(script, out scriptItem))
            {
                ScriptError(host,
                        "Could not set load bearing limit," +
                        " script not found.");
                return 0;
            }

            if (scriptItem.OwnerID !=
                    m_scene.RegionInfo.EstateSettings.EstateOwner)
            {
                LandData parcel = m_scene.GetLandData(host.AbsolutePosition);
                if (parcel == null)
                {
                    ScriptError(host,
                            "Could not set load bearing limit, parcel could" +
                            " not be found.");
                    return 0;
                }
                else if (scriptItem.OwnerID != parcel.OwnerID)
                {
                    ScriptError(host,
                            "Could not set load bearing limit, script owner" +
                            " does not match estate owner or parcel owner.");
                    return 0;
                }
            }

            UUID agentID;
            if (!UUID.TryParse(agent, out agentID))
            {
                ScriptError(host,
                        "Could not set load bearing limit," +
                        " invalid agent key.");
                return 0;
            }

            ScenePresence agentPresence = null;
            if (!m_scene.TryGetScenePresence(agentID, out agentPresence))
            {
                ScriptError(host,
                        "Could not set load bearing limit, agent not found.");
                return 0;
            }

            m_loadBearingLimits[agentID] = limit;
            return 1;
        }

        /// <summary>
        /// Gets the load bearing limit of the specified avatar.
        /// </summary>
        /// <param name="host"></param>
        /// <param name="script"></param>
        /// <param name="agent"></param>
        /// <returns></returns>
        [ScriptInvocation]
        public float tsuccirGetLoadBearingLimit(UUID hostID, UUID script,
                string agent)
        {
            UUID agentID;
            if (!UUID.TryParse(agent, out agentID))
            {
                string errorMsg = "Could not set attachment points as being" +
                        " occupied, agent key was invalid.";
                SceneObjectPart host = null;
                if (!m_scene.TryGetSceneObjectPart(hostID, out host))
                {
                    ScriptError(host, errorMsg);
                }
                else
                {
                    ScriptError(hostID, "unknown", new Vector3(m_scene.Center),
                            errorMsg);
                }
                return 0;
            }

            if (m_loadBearingLimits.ContainsKey(agentID))
                return m_loadBearingLimits[agentID];
            else
                return 0;
        }

        private bool SetMass(SceneObjectPart sop, float mass)
        {
            m_objectMass[sop.ParentGroup.UUID] = mass;
            return true;
        }

        /// <summary>
        /// Lets the object set it's own mass.
        /// </summary>
        /// <remarks>
        /// This is not intended to affect the physics engine.
        /// Will not update the mass of objects cloned from this object.
        /// </remarks>
        /// <param name="host"></param>
        /// <param name="script"></param>
        /// <param name="mass"></param>
        /// <returns></returns>
        [ScriptInvocation]
        public int tsuccirSetMass(UUID hostID, UUID script, float mass)
        {
            SceneObjectPart sop;
            if (!m_scene.TryGetSceneObjectPart(hostID, out sop))
            {
                ScriptError(hostID, "unknown", new Vector3(m_scene.Center),
                        "Cannot set mass on non-existant object.");
                return 0;
            }
            return SetMass(sop, mass) ? 1 : 0;
        }

        /// <summary>
        /// Sets the mass of another object.
        /// </summary>
        /// <remarks>
        /// Will only succeed on objects that are:
        /// * In the scene
        /// * This object or rezzed by this object
        /// </remarks>
        /// <param name="host"></param>
        /// <param name="script"></param>
        /// <param name="objectKey"></param>
        /// <param name="mass"></param>
        /// <returns></returns>
        [ScriptInvocation]
        public int tsuccirSetObjectMass(UUID hostID, UUID script,
                string objectKey, float mass)
        {
            UUID objectID = UUID.Zero;
            SceneObjectPart sop;
            if (!UUID.TryParse(objectKey, out objectID))
            {
                ScriptError(hostID, "unknown", new Vector3(m_scene.Center),
                        "Object key is not valid.");
                return 0;
            }
            else if (!m_scene.TryGetSceneObjectPart(objectID, out sop))
            {
                ScriptError(hostID, "unknown", new Vector3(m_scene.Center),
                        "Cannot set mass on non-existant object.");
                return 0;
            }
            else if (hostID != sop.UUID)
            {
                SceneObjectPart host;
                if (!m_scene.TryGetSceneObjectPart(hostID, out host))
                {
                    ScriptError(hostID, "unknown",
                            new Vector3(m_scene.Center),
                            "Cannot get host object.");
                    return 0;
                }
                else if (sop.ParentGroup.FromPartID != host.UUID &&
                        !PrimDescendants.CheckPrimDescendants(
                        host.ParentGroup.UUID, objectID, true))
                {
                    ScriptError(host,
                            "Cannot set the mass of other objects if" +
                            " this object did not rez it.");
                }
            }
            return SetMass(sop, mass) ? 1 : 0;
        }

        private float GetMass(UUID hostID)
        {
            if (hostID != UUID.Zero && !m_objectMass.ContainsKey(hostID))
            {
                SceneObjectPart sop;
                if (m_scene.TryGetSceneObjectPart(hostID, out sop))
                {
                    hostID = sop.ParentGroup.UUID;
                    if (!m_objectMass.ContainsKey(hostID))
                        foreach (UUID ancestor in
                                PrimDescendants.GetAncestors(sop.ParentGroup))
                        {
                            if (m_objectMass.ContainsKey(ancestor))
                                m_objectMass[hostID] = m_objectMass[ancestor];
                        }
                }
            }

            return m_objectMass.ContainsKey(hostID) ?
                    m_objectMass[hostID] : 0.0f;
        }

        /// <summary>
        /// Gets the current object mass
        /// </summary>
        /// <param name="host"></param>
        /// <param name="script"></param>
        /// <returns></returns>
        [ScriptInvocation]
        public float tsuccirGetMass(UUID hostID, UUID script)
        {
            return GetMass(hostID);
        }

        /// <summary>
        /// Gets the mass of another object.
        /// </summary>
        /// <param name="host"></param>
        /// <param name="script"></param>
        /// <param name="objectKey"></param>
        /// <returns></returns>
        [ScriptInvocation]
        public float tsuccirGetObjectMass(UUID hostID, UUID script,
                string objectKey)
        {
            UUID objectID;
            if (!UUID.TryParse(objectKey, out objectID))
                return 0.0f;
            else
                return GetMass(objectID);
        }

        #endregion

        #region occupied attachment points

        private Dictionary<UUID, Dictionary<UUID, List<int>>> m_occupiedAttachPoints = new Dictionary<UUID, Dictionary<UUID, List<int>>>();

        /// <summary>
        /// Ensures <seealso cref="m_occupiedAttachPoints"/> is initialised
        /// for the scene presence before adding to the list.
        /// </summary>
        /// <param name="sp"></param>
        private void InitOccupiedAttachmentPoints(ScenePresence presence)
        {
            if (!m_occupiedAttachPoints.ContainsKey(presence.UUID))
            {
                m_occupiedAttachPoints[presence.UUID] = new Dictionary<UUID, List<int>>();
            }
        }

        /// <summary>
        /// Ensures <seealso cref="m_occupiedAttachPoints"/> is initialised
        /// for the scene presence and host ID before adding to the list.
        /// </summary>
        /// <param name="sp"></param>
        private void InitOccupiedAttachmentPoints(ScenePresence presence,
                UUID host)
        {
            InitOccupiedAttachmentPoints(presence);
            if (!m_occupiedAttachPoints[presence.UUID].ContainsKey(host))
            {
                m_occupiedAttachPoints[presence.UUID][host] = new List<int>();
            }
        }

        /// <summary>
        /// Deduplicates the code for validating the host & agent key
        /// params.
        /// </summary>
        /// <param name="host"></param>
        /// <param name="agent"></param>
        /// <param name="requireMatchingAttachment"></param>
        /// <returns></returns>
        private ScenePresence GetPresenceForOccupiedAttachmentOp(
                UUID hostID, string agent, bool requireMatchingAttachment)
        {
            SceneObjectPart host = null;
            if (!m_scene.TryGetSceneObjectPart(hostID, out host))
            {
                ScriptError(hostID, "unknown", new Vector3(m_scene.Center),
                        "Host object disappeared.");
                return null;
            }

            if (requireMatchingAttachment && !host.ParentGroup.IsAttachment)
            {
                ScriptError(host, "Host object is not an attachment.");
                return null;
            }

            UUID agentID;
            if (!UUID.TryParse(agent, out agentID))
            {
                ScriptError(host, "Agent key was invalid.");
                return null;
            }

            ScenePresence agentPresence;
            if (!m_scene.TryGetScenePresence(agentID, out agentPresence))
            {
                ScriptError(host, "Could not find agent.");
                return null;
            }

            if (requireMatchingAttachment &&
                    host.OwnerID != agentPresence.UUID)
            {
                ScriptError(host,
                        "An agent's attachments can only mark attachment" +
                        " points as being occupied for their own avatar.");
                return null;
            }

            return agentPresence;
        }

        /// <summary>
        /// Marks attachment points as being occupied.
        /// </summary>
        /// <param name="host"></param>
        /// <param name="script"></param>
        /// <param name="agent"></param>
        /// <param name="attachmentPointList">
        /// Should be a list of attachment points.
        /// </param>
        /// <returns></returns>
        [ScriptInvocation]
        public int tsuccirSetAttachmentPointsAsOccupied(UUID host,
                UUID script, string agent, object[] attachmentPointList)
        {
            ScenePresence sp = GetPresenceForOccupiedAttachmentOp(host, agent,
                    true);

            if (sp == null)
                return 0;

            InitOccupiedAttachmentPoints(sp, host);

            m_occupiedAttachPoints[sp.UUID][host] = LSLUtil.AttachPoints(
                    attachmentPointList);

            return 1;
        }

        /// <summary>
        /// Marks attachment points as being unoccupied.
        /// </summary>
        /// <param name="host"></param>
        /// <param name="script"></param>
        /// <param name="agent"></param>
        /// <param name="attachmentPointList"></param>
        /// <returns></returns>
        [ScriptInvocation]
        public int tsuccirSetAttachmentPointsAsUnoccupied(UUID hostID,
                UUID script, string agent, object[] attachmentPointList)
        {
            ScenePresence presence =
                    GetPresenceForOccupiedAttachmentOp(hostID, agent, true);

            if (presence == null)
                return 0;
            else if (!m_occupiedAttachPoints.ContainsKey(presence.UUID))
                return 1;

            List<int> attachmentPoints = LSLUtil.TypedList<int>(
                    attachmentPointList, 0);

            m_occupiedAttachPoints[presence.UUID].Remove(hostID);

            return 1;
        }

        /// <summary>
        /// Determine if a given attachment point is occupied.
        /// </summary>
        /// <param name="host"></param>
        /// <param name="script"></param>
        /// <param name="agent"></param>
        /// <param name="attachmentPoint"></param>
        /// <returns></returns>
        [ScriptInvocation]
        public int tsuccirAreAttachmentPointsOccupied(UUID hostID,
                UUID script, string agent, object[] attachmentPointList)
        {
            ScenePresence presence =
                    GetPresenceForOccupiedAttachmentOp(hostID, agent, false);

            if (presence == null)
                return 0;

            InitOccupiedAttachmentPoints(presence);

            if (!m_occupiedAttachPoints[presence.UUID].ContainsKey(hostID))
                return 0;

            List<int> attachmentPoints = LSLUtil.TypedList<int>(
                    attachmentPointList, 0);

            foreach (int attachmentPoint in attachmentPointList)
            {
                if (m_occupiedAttachPoints[presence.UUID][hostID].Contains(
                        attachmentPoint))
                    return 1;
            }
            return 0;
        }

        #endregion

        #endregion
    }
}
