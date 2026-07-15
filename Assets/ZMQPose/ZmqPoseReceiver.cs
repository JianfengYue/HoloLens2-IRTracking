using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using NetMQ;
using NetMQ.Sockets;

namespace ZmqPoseSystem
{
    public class ZmqPoseReceiver : MonoBehaviour
    {
        [Header("Pose Manager")]
        public PoseManager poseManager;

        [Header("Optional Sender For Status Replies")]
        public ZmqPoseSender sender;

        [Header("Python Network Settings")]
        public string pythonHost = "127.0.0.1";

        [Tooltip("Python PUSH bind port. Unity PULL connects to this port.")]
        public int commandPort = 5555;

        [Header("Receiving")]
        public int maxMessagesPerFrame = 20;

        public bool logRawMessages = false;

        private Thread receiveThread;

        private volatile bool running;

        private bool cleanedUp;

        private readonly object messageLock = new object();

        private readonly Queue<string> pendingMessages =
            new Queue<string>();

        private string CommandAddress
        {
            get
            {
                return "tcp://" + pythonHost + ":" + commandPort;
            }
        }

        private void Start()
        {
            ZmqPoseRuntime.Initialize();

            if (poseManager == null)
            {
                poseManager = FindObjectOfType<PoseManager>();
            }

            if (sender == null)
            {
                sender = GetComponent<ZmqPoseSender>();
            }

            running = true;

            receiveThread = new Thread(ReceiveLoop);
            receiveThread.IsBackground = true;
            receiveThread.Start();

            Debug.Log(
                "ZmqPoseReceiver started. Unity PULL connecting to: " +
                CommandAddress
            );
        }

        private void ReceiveLoop()
        {
            try
            {
                using (var pullSocket = new PullSocket())
                {
                    pullSocket.Options.ReceiveHighWatermark = 100;
                    pullSocket.Connect(CommandAddress);

                    while (running)
                    {
                        string message;

                        bool received =
                            pullSocket.TryReceiveFrameString(
                                TimeSpan.FromMilliseconds(100),
                                out message
                            );

                        if (received)
                        {
                            lock (messageLock)
                            {
                                pendingMessages.Enqueue(message);
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (running)
                {
                    Debug.LogError(
                        "ZmqPoseReceiver receive error: " +
                        e.Message
                    );
                }
            }
        }

        private void Update()
        {
            ProcessPendingMessages();
        }

        private void ProcessPendingMessages()
        {
            int processed = 0;

            while (processed < maxMessagesPerFrame)
            {
                string message = null;

                lock (messageLock)
                {
                    if (pendingMessages.Count > 0)
                    {
                        message = pendingMessages.Dequeue();
                    }
                }

                if (message == null)
                {
                    break;
                }

                ApplyMessage(message);

                processed++;
            }
        }

        private void ApplyMessage(string json)
        {
            if (logRawMessages)
            {
                Debug.Log("ZMQ received: " + json);
            }

            if (poseManager == null)
            {
                Debug.LogWarning(
                    "ZmqPoseReceiver: poseManager is null."
                );

                SendStatusIfPossible(
                    "command_ignored_no_pose_manager"
                );

                return;
            }

            try
            {
                PoseFrameDto frame =
                    JsonUtility.FromJson<PoseFrameDto>(json);

                if (frame == null)
                {
                    return;
                }

                if (frame.ping)
                {
                    Debug.Log("ZMQ ping received from Python.");

                    SendStatusIfPossible("pong");
                }

                if (frame.poses == null ||
                    frame.poses.Length == 0)
                {
                    return;
                }

                int appliedCount = 0;

                foreach (PoseDto pose in frame.poses)
                {
                    bool applied =
                        poseManager.ApplyPose(pose);

                    if (applied)
                    {
                        appliedCount++;
                    }
                }

                if (appliedCount > 0)
                {
                    SendStatusIfPossible("command_applied");
                }
                else
                {
                    SendStatusIfPossible(
                        "command_received_no_pose_applied"
                    );
                }
            }
            catch (Exception e)
            {
                Debug.LogError(
                    "ZmqPoseReceiver JSON parse error: " +
                    e.Message
                );

                Debug.LogError("Raw message: " + json);

                SendStatusIfPossible("command_parse_error");
            }
        }

        private void SendStatusIfPossible(string statusType)
        {
            if (sender != null)
            {
                sender.SendStatus(statusType);
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
            running = false;

            if (receiveThread != null &&
                receiveThread.IsAlive)
            {
                receiveThread.Join(500);
            }

            ZmqPoseRuntime.Shutdown();
        }
    }
}