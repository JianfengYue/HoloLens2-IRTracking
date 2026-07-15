using System.Collections.Generic;
using UnityEngine;

namespace ZmqPoseSystem
{
    public class PoseManager : MonoBehaviour
    {
        [Header("Scene Objects")]
        [Tooltip("Drag Unity objects here. Incoming JSON id must match the GameObject name.")]
        public Transform[] sceneObjects;

        private readonly Dictionary<string, Transform> objectMap =
            new Dictionary<string, Transform>();

        private readonly List<Transform> registeredObjects =
            new List<Transform>();

        private void Awake()
        {
            RebuildObjectMap();
        }

        public void RebuildObjectMap()
        {
            objectMap.Clear();
            registeredObjects.Clear();

            if (sceneObjects == null)
            {
                Debug.LogWarning("PoseManager: sceneObjects is null.");
                return;
            }

            foreach (Transform obj in sceneObjects)
            {
                if (obj == null)
                {
                    continue;
                }

                string id = obj.name;

                if (objectMap.ContainsKey(id))
                {
                    Debug.LogWarning(
                        "PoseManager: duplicate object name ignored: " + id
                    );

                    continue;
                }

                objectMap.Add(id, obj);
                registeredObjects.Add(obj);
            }

            Debug.Log(
                "PoseManager registered " +
                objectMap.Count +
                " objects."
            );
        }

        public Transform GetObject(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            Transform obj;

            if (objectMap.TryGetValue(id, out obj))
            {
                return obj;
            }

            return null;
        }

        public Transform[] GetRegisteredObjects()
        {
            return registeredObjects.ToArray();
        }

        public bool ApplyPose(PoseDto pose)
        {
            if (pose == null)
            {
                return false;
            }

            Transform target = GetObject(pose.id);

            if (target == null)
            {
                Debug.LogWarning(
                    "PoseManager: object not found: " + pose.id
                );

                return false;
            }

            Quaternion rotation =
                ToQuaternionOrIdentity(pose.rotation);

            bool useRelativePose =
                pose.relative ||
                !string.IsNullOrEmpty(pose.parentId);

            if (useRelativePose)
            {
                return ApplyRelativePose(
                    target,
                    pose.parentId,
                    pose.position,
                    rotation
                );
            }

            target.SetPositionAndRotation(
                pose.position,
                rotation
            );

            return true;
        }

        private bool ApplyRelativePose(
            Transform target,
            string parentId,
            Vector3 localPosition,
            Quaternion localRotation)
        {
            Transform parent = GetObject(parentId);

            if (parent == null)
            {
                Debug.LogWarning(
                    "PoseManager: parent object not found: " + parentId
                );

                return false;
            }

            Vector3 worldPosition =
                parent.TransformPoint(localPosition);

            Quaternion worldRotation =
                parent.rotation * localRotation;

            target.SetPositionAndRotation(
                worldPosition,
                worldRotation
            );

            return true;
        }

        public static Quaternion ToQuaternionOrIdentity(
            PoseQuaternionDto payload)
        {
            if (payload == null)
            {
                return Quaternion.identity;
            }

            float magnitude =
                Mathf.Sqrt(
                    payload.x * payload.x +
                    payload.y * payload.y +
                    payload.z * payload.z +
                    payload.w * payload.w
                );

            if (magnitude < 0.000001f)
            {
                return Quaternion.identity;
            }

            return new Quaternion(
                payload.x / magnitude,
                payload.y / magnitude,
                payload.z / magnitude,
                payload.w / magnitude
            );
        }

        public static PoseQuaternionDto FromQuaternion(
            Quaternion q)
        {
            return new PoseQuaternionDto
            {
                x = q.x,
                y = q.y,
                z = q.z,
                w = q.w
            };
        }
    }
}