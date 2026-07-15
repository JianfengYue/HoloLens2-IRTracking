using System;
using UnityEngine;
using NetMQ;

namespace ZmqPoseSystem
{
    [Serializable]
    public class PoseQuaternionDto
    {
        public float x;
        public float y;
        public float z;
        public float w;
    }

    [Serializable]
    public class PoseDto
    {
        public string id;

        public bool relative;

        public string parentId;

        public Vector3 position;

        public PoseQuaternionDto rotation;
    }

    [Serializable]
    public class PoseFrameDto
    {
        public string type;

        public PoseDto[] poses;

        public bool ping;
    }

    [Serializable]
    public class UnityPoseDto
    {
        public string id;

        public Vector3 position;

        public PoseQuaternionDto rotation;
    }

    [Serializable]
    public class UnityPoseFrameDto
    {
        public string type;

        public string timestamp;

        public UnityPoseDto[] poses;

        public string note;
    }

    [Serializable]
    public class LegacyUnityMessageDto
    {
        public string type;

        public string markerName;

        public string timestamp;

        public Vector3 position;

        public PoseQuaternionDto rotation;

        public string note;
    }

    public enum PoseSendFormat
    {
        PoseFrame,
        LegacySinglePose,
        Both
    }

    public static class ZmqPoseRuntime
    {
        private static readonly object RuntimeLock = new object();

        private static int activeUsers = 0;

        public static void Initialize()
        {
            lock (RuntimeLock)
            {
                if (activeUsers == 0)
                {
                    AsyncIO.ForceDotNet.Force();
                }

                activeUsers++;
            }
        }

        public static void Shutdown()
        {
            lock (RuntimeLock)
            {
                activeUsers--;

                if (activeUsers < 0)
                {
                    activeUsers = 0;
                }

                if (activeUsers == 0)
                {
                    NetMQConfig.Cleanup(false);
                }
            }
        }
    }
}