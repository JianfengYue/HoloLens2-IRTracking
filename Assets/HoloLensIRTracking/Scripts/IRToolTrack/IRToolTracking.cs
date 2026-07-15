
using UnityEngine;
using System;
using IRToolTrack;
using System.Linq;
using System.Runtime.InteropServices;


#if ENABLE_WINMD_SUPPORT
using System.Threading.Tasks;
using HL2IRToolTracking;
#endif

public class IRToolTracking : MonoBehaviour
{
#if ENABLE_WINMD_SUPPORT
    HL2IRTracking toolTracking;
#endif
    private bool startToolTracking = false;

    private IRToolController[] tools = null;

    public float[] GetToolTransform(string identifier)
    {
        var toolTransform = Enumerable.Repeat<float>(0, 8).ToArray();
#if ENABLE_WINMD_SUPPORT
        if (toolTracking == null)
        {
            Debug.LogWarning("IR tool tracking has not been initialized yet.");
            return toolTransform;
        }
        toolTransform = toolTracking.GetToolTransform(identifier);
#endif
        return toolTransform;

    }

    public Int64 GetTimestamp()
    {
#if ENABLE_WINMD_SUPPORT
        if (toolTracking == null)
        {
            return 0;
        }
        return toolTracking.GetTrackingTimestamp();
#else
        return 0;
#endif
    }

    public void Start()
    {
        //Find Tool Controllers and add them to the tracking
        tools = FindObjectsOfType<IRToolController>();
        StartToolTracking();
    }

    public void StartToolTracking()
    {
        Debug.Log("Start Tracking");
#if !ENABLE_WINMD_SUPPORT
        Debug.LogWarning("IR tracking only runs in a UWP device build with ENABLE_WINMD_SUPPORT. The Unity Editor will return empty poses.");
#endif
#if ENABLE_WINMD_SUPPORT
        if (!startToolTracking){
            if (toolTracking == null)
            {
                toolTracking = new HL2IRTracking();
            }
            if (!SetReferenceWorldCoordinateSystem())
            {
                Debug.LogError("Could not set the Unity world coordinate system for IR tracking.");
                return;
            }

            if (!toolTracking.RemoveAllToolDefinitions())
            {
                Debug.LogWarning("Could not clear previous IR tool definitions.");
            }

            if (tools == null || tools.Length == 0)
            {
                Debug.LogWarning("No IRToolController components found in the scene.");
                return;
            }

            foreach (IRToolController tool in tools)
            {
                if (tool == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(tool.identifier))
                {
                    Debug.LogError($"Skipping IR tool on {tool.name}: identifier is empty.");
                    continue;
                }

                if (tool.spheres == null || tool.sphere_count < 3 || tool.spheres.Any(sphere => sphere == null))
                {
                    Debug.LogError($"Skipping IR tool {tool.identifier}: at least three sphere GameObjects are required.");
                    continue;
                }

                int min_visible_spheres = tool.sphere_count;
                if (tool.max_occluded_spheres > 0 && (tool.sphere_count - tool.max_occluded_spheres) >= 3)
                {
                    min_visible_spheres = tool.sphere_count - tool.max_occluded_spheres;
                }
                bool definitionAdded = toolTracking.AddToolDefinition(tool.sphere_count, tool.sphere_positions, tool.sphere_radius, tool.identifier, min_visible_spheres, tool.lowpass_factor_rotation, tool.lowpass_factor_position);
                if (definitionAdded)
                {
                    Debug.Log($"Added IR tool definition {tool.identifier} with {tool.sphere_count} spheres, radius {tool.sphere_radius}, min visible {min_visible_spheres}.");
                    tool.StartTracking();
                }
                else
                {
                    Debug.LogError($"Native IR plugin rejected tool definition {tool.identifier}. Check sphere count, local positions, radius, and identifier.");
                }
            }

            if (!tools.Any(tool => tool != null && tool.StatusIsActive))
            {
                Debug.LogError("No valid IR tool definitions were added; tracking was not started.");
                return;
            }

            if (!toolTracking.StartToolTracking())
            {
                Debug.LogError("Native IR plugin failed to start. Check HoloLens Research Mode, app capabilities, and ARM64/UWP deployment.");
                return;
            }
            startToolTracking = true;
        }
#endif
    }

    public void StopToolTracking()
    {
        if (!startToolTracking)
        {
            Debug.Log("Tracking was not started, so cannot stop it");
            return;
        }

#if ENABLE_WINMD_SUPPORT
        var success = toolTracking.StopToolTracking();
        if (!success)
        {
            Debug.Log("Could not stop tracking");
        }
        startToolTracking = false;
        foreach (IRToolController tool in tools)
        {
            tool.StopTracking();
        }
#endif
        Debug.Log("Stopped Tracking");
    }

    private bool SetReferenceWorldCoordinateSystem()
    {
        print("Setting World Coordinate");
#if ENABLE_WINMD_SUPPORT
        // Get Unity Origin Coordinate
#if UNITY_2021_2_OR_NEWER
        //var unityWorldOrigin = Microsoft.Windows.Perception.Spatial.SpatialCoordinateSystem
        Windows.Perception.Spatial.SpatialCoordinateSystem unityWorldOrigin = Microsoft.MixedReality.OpenXR.PerceptionInterop.GetSceneCoordinateSystem(UnityEngine.Pose.identity) as Windows.Perception.Spatial.SpatialCoordinateSystem;
#elif UNITY_2020_1_OR_NEWER
        IntPtr WorldOriginPtr = UnityEngine.XR.WindowsMR.WindowsMREnvironment.OriginSpatialCoordinateSystem;
        var unityWorldOrigin = Marshal.GetObjectForIUnknown(WorldOriginPtr) as Windows.Perception.Spatial.SpatialCoordinateSystem;
#else
        IntPtr WorldOriginPtr = UnityEngine.XR.WSA.WorldManager.GetNativeISpatialCoordinateSystemPtr();
        var unityWorldOrigin = Marshal.GetObjectForIUnknown(WorldOriginPtr) as Windows.Perception.Spatial.SpatialCoordinateSystem;
#endif
        if (unityWorldOrigin == null)
        {
            return false;
        }
        // Set Unity Origin Coordinate
        toolTracking.SetReferenceCoordinateSystem(unityWorldOrigin);
        return true;
#else
        return false;
#endif


    }

    public void ExitApplication()
    {
        StopToolTracking();
        Application.Quit();
    }
}
