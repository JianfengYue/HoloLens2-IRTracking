using System;
using System.Collections.Generic;
using UnityEngine;
using NetMQ;
using NetMQ.Sockets;

namespace ZmqPoseSystem
{
    public class ZmqPoseSender : MonoBehaviour
    {
        [Header("Pose Manager")]
        public PoseManager poseManager;

        [Header("Python Network Settings")]
        public string pythonHost = "127.0.0.1";

        [Tooltip("Python PULL bind port. Unity PUSH connects to this port.")]
        public int unityToPythonPort = 5556;

        [Header("Pose Sending")]
        public bool sendPoseToPython = true;

        [Tooltip("0.05 = 20 Hz. 1.0 = once per second for testing.")]
        public float sendIntervalSeconds = 0.05f;

        [Tooltip("If empty, all PoseManager objects will be sent.")]
        public Transform[] objectsToSendToPython;

        [Header("Send Format")]
        public PoseSendFormat sendFormat =
            PoseSendFormat.PoseFrame;

        [Header("Legacy Single Pose Settings")]
        public Transform legacySinglePoseObject;

        public string legacyMarkerNameOverride = "";

        private PushSocket pushSocket;

        private bool cleanedUp;

        private float nextPoseSendTime;

        private float nextHeartbeatTime;

        private string UnityToPythonAddress
        {
            get
            {
                return "tcp://" +
                       pythonHost +
                       ":" +
                       unityToPythonPort;
            }
        }

        private void Start()
        {
            ZmqPoseRuntime.Initialize();

            if (poseManager == null)
            {
                poseManager = FindObjectOfType<PoseManager>();
            }

            try
            {
                pushSocket = new PushSocket();
                pushSocket.Options.SendHighWatermark = 100;
                pushSocket.Connect(UnityToPythonAddress);

                Debug.Log(
                    "ZmqPoseSender started. Unity PUSH connecting to: " +
                    UnityToPythonAddress
                );
            }
            catch (Exception e)
            {
                Debug.LogError(
                    "ZmqPoseSender setup error: " +
                    e.Message
                );
            }

            SendStatus("unity_started");
        }

        private void Update()
        {
            SendPoseIfNeeded();
            SendHeartbeatIfNeeded();
        }

        private void SendPoseIfNeeded()
        {
            if (!sendPoseToPython)
            {
                return;
            }

            float interval =
                Mathf.Max(0.001f, sendIntervalSeconds);

            if (Time.unscaledTime < nextPoseSendTime)
            {
                return;
            }

            nextPoseSendTime =
                Time.unscaledTime + interval;

            if (sendFormat == PoseSendFormat.PoseFrame ||
                sendFormat == PoseSendFormat.Both)
            {
                SendPoseFrame("pose_frame");
            }

            if (sendFormat == PoseSendFormat.LegacySinglePose ||
                sendFormat == PoseSendFormat.Both)
            {
                SendLegacySinglePose("pose", "");
            }
        }

        private void SendHeartbeatIfNeeded()
        {
            if (Time.unscaledTime < nextHeartbeatTime)
            {
                return;
            }

            nextHeartbeatTime =
                Time.unscaledTime + 1.0f;

            SendStatus("heartbeat");
        }

        public void SendStatus(string statusType)
        {
            if (sendFormat == PoseSendFormat.PoseFrame ||
                sendFormat == PoseSendFormat.Both)
            {
                UnityPoseFrameDto frame =
                    new UnityPoseFrameDto
                    {
                        type = statusType,
                        timestamp = DateTime.UtcNow.ToString("o"),
                        poses = new UnityPoseDto[0],
                        note = "Unity status message"
                    };

                SendJson(JsonUtility.ToJson(frame));
            }

            if (sendFormat == PoseSendFormat.LegacySinglePose ||
                sendFormat == PoseSendFormat.Both)
            {
                SendLegacySinglePose(
                    statusType,
                    "Unity status message"
                );
            }
        }

        private void SendPoseFrame(string type)
        {
            Transform[] targets =
                GetPoseSendingTargets();

            List<UnityPoseDto> poseItems =
                new List<UnityPoseDto>();

            foreach (Transform obj in targets)
            {
                if (obj == null)
                {
                    continue;
                }

                UnityPoseDto item =
                    new UnityPoseDto
                    {
                        id = obj.name,
                        position = obj.position,
                        rotation =
                            PoseManager.FromQuaternion(
                                obj.rotation
                            )
                    };

                poseItems.Add(item);
            }

            UnityPoseFrameDto frame =
                new UnityPoseFrameDto
                {
                    type = type,
                    timestamp = DateTime.UtcNow.ToString("o"),
                    poses = poseItems.ToArray(),
                    note = ""
                };

            SendJson(JsonUtility.ToJson(frame));
        }

        private void SendLegacySinglePose(
            string type,
            string note)
        {
            Transform obj =
                GetLegacyPoseObject();

            Vector3 position = Vector3.zero;
            Quaternion rotation = Quaternion.identity;
            string markerName = "none";

            if (obj != null)
            {
                position = obj.position;
                rotation = obj.rotation;
                markerName = obj.name;
            }

            if (!string.IsNullOrEmpty(legacyMarkerNameOverride))
            {
                markerName = legacyMarkerNameOverride;
            }

            LegacyUnityMessageDto message =
                new LegacyUnityMessageDto
                {
                    type = type,
                    markerName = markerName,
                    timestamp = DateTime.UtcNow.ToString("o"),
                    position = position,
                    rotation =
                        PoseManager.FromQuaternion(rotation),
                    note = note
                };

            SendJson(JsonUtility.ToJson(message));
        }

        private Transform[] GetPoseSendingTargets()
        {
            if (objectsToSendToPython != null &&
                objectsToSendToPython.Length > 0)
            {
                return objectsToSendToPython;
            }

            if (poseManager != null)
            {
                return poseManager.GetRegisteredObjects();
            }

            return new Transform[0];
        }

        private Transform GetLegacyPoseObject()
        {
            if (legacySinglePoseObject != null)
            {
                return legacySinglePoseObject;
            }

            if (objectsToSendToPython != null &&
                objectsToSendToPython.Length > 0 &&
                objectsToSendToPython[0] != null)
            {
                return objectsToSendToPython[0];
            }

            if (poseManager != null)
            {
                Transform[] registeredObjects =
                    poseManager.GetRegisteredObjects();

                if (registeredObjects.Length > 0)
                {
                    return registeredObjects[0];
                }
            }

            return null;
        }

        private void SendJson(string json)
        {
            if (pushSocket == null)
            {
                return;
            }

            try
            {
                bool sent =
                    pushSocket.TrySendFrame(
                        TimeSpan.FromMilliseconds(10),
                        json
                    );

                if (!sent)
                {
                    Debug.LogWarning(
                        "ZMQ Unity->Python send timed out. Address: " +
                        UnityToPythonAddress
                    );
                }
            }
            catch (Exception e)
            {
                Debug.LogError(
                    "ZmqPoseSender send error: " +
                    e.Message
                );
            }
        }

        private void OnApplicationQuit()
        {
            StopWorker();
        }

        private void OnDestroy()
        {
            StopWorker();
        }

        private void StopWorker()
        {
            if (cleanedUp)
            {
                return;
            }

            cleanedUp = true;

            if (pushSocket != null)
            {
                pushSocket.Close();
                pushSocket.Dispose();
                pushSocket = null;
            }

            ZmqPoseRuntime.Shutdown();
        }
    }
}