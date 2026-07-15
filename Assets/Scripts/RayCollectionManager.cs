using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Microsoft.MixedReality.Toolkit.Input;
using Microsoft.MixedReality.Toolkit.Utilities;

public class RayCollectionManager : MonoBehaviour
{
    [Header("Ray Display")]
    [Min(0.1f)]
    public float rayLength = 3.0f;

    [Min(0.001f)]
    public float lineWidth = 0.008f;

    [Header("Windows Receiver")]
    public string windowsIp = "192.168.1.100";

    [Range(1, 65535)]
    public int windowsPort = 5000;

    [Header("Reference Point Groups")]
    [Tooltip("Assign f1, f2, f3, f4 in this exact order.")]
    public Transform[] femurMarkers = new Transform[4];

    [Tooltip("Assign t1, t2, t3, t4 in this exact order.")]
    public Transform[] tibiaMarkers = new Transform[4];

    [Tooltip("Assign m1, m2, m3, m4 in this exact order.")]
    public Transform[] markerMarkers = new Transform[4];

    private const int PointCount = 4;
    private const int RaysPerPoint = 4;

    private SessionData session;
    private int currentPointIndex = 0;

    private readonly List<List<GameObject>> displayedLines =
        new List<List<GameObject>>();

    private Transform rayContainer;
    private Material lineMaterial;

    private string statusMessage = "Ready.";
    private bool isSending = false;

    private void Start()
    {
        CreateEmptySession();
        CreateRayContainer();
        CreateLineMaterial();

        statusMessage =
            "Show the right hand. Release Space to capture Ray 1.";
    }

    private void Update()
    {
        if (isSending)
        {
            return;
        }

        /*
         * MRTK Editor input simulation uses Space to manipulate
         * the simulated right hand.
         *
         * For that reason, this program captures the ray when
         * Space is released instead of when Space is first pressed.
         */
        if (Input.GetKeyUp(KeyCode.Space))
        {
            CaptureRay();
        }

        if (Input.GetKeyDown(KeyCode.Backspace))
        {
            UndoLastRay();
        }

        if (Input.GetKeyDown(KeyCode.Return) ||
            Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            SendAllData();
        }

        if (Input.GetKeyDown(KeyCode.Delete))
        {
            DeleteAllData();
        }
    }

    private void OnDestroy()
    {
        if (lineMaterial != null)
        {
            Destroy(lineMaterial);
        }
    }

    // =========================================================
    // Create the empty 4 x 4 collection
    // =========================================================

    private void CreateEmptySession()
    {
        session = new SessionData
        {
            groups = new List<PointGroup>()
        };

        displayedLines.Clear();

        for (int pointIndex = 0;
             pointIndex < PointCount;
             pointIndex++)
        {
            session.groups.Add(
                new PointGroup
                {
                    pointIndex = pointIndex,
                    rays = new List<RayData>()
                });

            displayedLines.Add(new List<GameObject>());
        }

        currentPointIndex = 0;
    }

    private void CreateRayContainer()
    {
        GameObject containerObject =
            new GameObject("CapturedRays");

        containerObject.transform.SetParent(
            transform,
            false);

        rayContainer = containerObject.transform;
    }

    private void CreateLineMaterial()
    {
        Shader shader = Shader.Find("Sprites/Default");

        if (shader == null)
        {
            shader = Shader.Find("Unlit/Color");
        }

        if (shader == null)
        {
            Debug.LogWarning(
                "No suitable line shader was found. " +
                "Captured lines may appear pink.");

            return;
        }

        lineMaterial = new Material(shader);
    }

    // =========================================================
    // Space: capture one MRTK right-hand ray
    // =========================================================

    private void CaptureRay()
    {
        PointGroup currentPoint =
            session.groups[currentPointIndex];

        if (currentPoint.rays.Count >= RaysPerPoint)
        {
            statusMessage =
                "This point already has 4 rays.";

            return;
        }

        if (!TryGetRightHandRay(
                out Vector3 pointA,
                out Vector3 pointB))
        {
            statusMessage =
                "Right hand ray not found. " +
                "Show the right hand and try again.";

            Debug.LogWarning(
                "No active MRTK right-hand line pointer was found.");

            return;
        }

        RayData newRay = new RayData
        {
            pointA = pointA,
            pointB = pointB
        };

        currentPoint.rays.Add(newRay);

        GameObject lineObject = CreateRayLine(
            pointA,
            pointB,
            currentPointIndex,
            currentPoint.rays.Count - 1);

        displayedLines[currentPointIndex].Add(
            lineObject);

        int currentRayNumber =
            currentPoint.rays.Count;

        if (currentRayNumber == RaysPerPoint)
        {
            if (currentPointIndex == PointCount - 1)
            {
                statusMessage =
                    "All 16 rays are complete. " +
                    "Press Enter to send.";
            }
        }
        else
        {
            statusMessage =
                "Ray " +
                currentRayNumber +
                " captured for Point " +
                (currentPointIndex + 1) +
                ".";
        }

        Debug.Log(
            "Captured Point " +
            (currentPointIndex + 1) +
            ", Ray " +
            currentRayNumber +
            "\nPoint A: " +
            pointA +
            "\nPoint B: " +
            pointB);

        if (currentRayNumber == RaysPerPoint &&
            currentPointIndex < PointCount - 1)
        {
            GoToNextPoint();
        }
    }

    private bool TryGetRightHandRay(
        out Vector3 pointA,
        out Vector3 pointB)
    {
        pointA = Vector3.zero;
        pointB = Vector3.zero;

        /*
         * Look for an MRTK line pointer that:
         * 1. Belongs to the right hand.
         * 2. Comes from hand tracking.
         * 3. Is currently active.
         */
        foreach (LinePointer pointer in
                 PointerUtils.GetPointers<LinePointer>(
                     Handedness.Right,
                     InputSourceType.Hand))
        {
            if (pointer == null ||
                !pointer.IsActive ||
                pointer.Rays == null ||
                pointer.Rays.Length == 0)
            {
                continue;
            }

            Vector3 direction =
                pointer.Rays[0].Direction;

            if (direction.sqrMagnitude < 0.000001f)
            {
                continue;
            }

            pointA =
                pointer.Rays[0].Origin;

            pointB =
                pointA +
                direction.normalized * rayLength;

            return true;
        }

        return false;
    }

    // =========================================================
    // Backspace: undo the most recent ray
    // =========================================================

    private void UndoLastRay()
    {
        PointGroup currentPoint =
            session.groups[currentPointIndex];

        if (currentPoint.rays.Count > 0)
        {
            RemoveLastRayFromPoint(
                currentPointIndex);

            statusMessage =
                "Last ray removed. Point " +
                (currentPointIndex + 1) +
                " now has " +
                session.groups[currentPointIndex]
                    .rays.Count +
                "/4 rays.";

            return;
        }

        /*
         * If the current point is empty, go back to the
         * previous point and remove its last ray.
         */
        if (currentPointIndex > 0)
        {
            currentPointIndex--;

            if (session.groups[currentPointIndex]
                    .rays.Count > 0)
            {
                RemoveLastRayFromPoint(
                    currentPointIndex);

                statusMessage =
                    "Returned to Point " +
                    (currentPointIndex + 1) +
                    " and removed its last ray.";
            }
            else
            {
                statusMessage =
                    "Returned to Point " +
                    (currentPointIndex + 1) +
                    ".";
            }

            return;
        }

        statusMessage =
            "There is no ray to remove.";
    }

    private void RemoveLastRayFromPoint(
        int pointIndex)
    {
        PointGroup point =
            session.groups[pointIndex];

        if (point.rays.Count == 0)
        {
            return;
        }

        point.rays.RemoveAt(
            point.rays.Count - 1);

        List<GameObject> lines =
            displayedLines[pointIndex];

        if (lines.Count == 0)
        {
            return;
        }

        int lastIndex =
            lines.Count - 1;

        GameObject lastLine =
            lines[lastIndex];

        lines.RemoveAt(lastIndex);

        if (lastLine != null)
        {
            Destroy(lastLine);
        }
    }

    // =========================================================
    // Automatically move to the next real point
    // =========================================================

    private void GoToNextPoint()
    {
        PointGroup currentPoint =
            session.groups[currentPointIndex];

        if (currentPoint.rays.Count < RaysPerPoint)
        {
            statusMessage =
                "The current point has only " +
                currentPoint.rays.Count +
                "/4 rays.";

            return;
        }

        if (currentPointIndex >= PointCount - 1)
        {
            statusMessage =
                "All points are complete. " +
                "Press Enter to send.";

            return;
        }

        currentPointIndex++;

        statusMessage =
            "Point " +
            (currentPointIndex + 1) +
            " selected. Release Space to capture.";
    }

    // =========================================================
    // Delete: clear all 4 points and all 16 rays
    // =========================================================

    private void DeleteAllData()
    {
        for (int pointIndex = 0;
            pointIndex < session.groups.Count;
            pointIndex++)
        {
            // Remove stored ray data.
            session.groups[pointIndex].rays.Clear();

            // Remove displayed ray lines.
            List<GameObject> lines =
                displayedLines[pointIndex];

            for (int lineIndex = 0;
                lineIndex < lines.Count;
                lineIndex++)
            {
                if (lines[lineIndex] != null)
                {
                    Destroy(lines[lineIndex]);
                }
            }

            lines.Clear();
        }

        // Return to the first point.
        currentPointIndex = 0;

        statusMessage =
            "All captured data was cleared. " +
            "Point 1 is ready.";

        Debug.Log(
            "All captured rays were cleared. " +
            "Collection returned to Point 1.");
    }

    // =========================================================
    // Read the 12 reference-point positions in Unity world space
    // Transform.position is the absolute/world-space coordinate.
    // =========================================================

    private bool TryUpdateBoneWorldPoints(
        out string errorMessage)
    {
        errorMessage = "";

        if (!TryCreateWorldPointList(
                femurMarkers,
                "Femur",
                "f",
                out List<WorldPointData> femurPoints,
                out errorMessage))
        {
            return false;
        }

        if (!TryCreateWorldPointList(
                tibiaMarkers,
                "Tibia",
                "t",
                out List<WorldPointData> tibiaPoints,
                out errorMessage))
        {
            return false;
        }

        if (!TryCreateWorldPointList(
                markerMarkers,
                "Marker",
                "m",
                out List<WorldPointData> markerPoints,
                out errorMessage))
        {
            return false;
        }

        session.bonePoints = new BoneWorldPointData
        {
            femur = femurPoints,
            tibia = tibiaPoints,
            marker = markerPoints
        };

        return true;
    }

    private bool TryCreateWorldPointList(
        Transform[] markers,
        string groupName,
        string pointPrefix,
        out List<WorldPointData> points,
        out string errorMessage)
    {
        points = new List<WorldPointData>();
        errorMessage = "";

        if (markers == null ||
            markers.Length != PointCount)
        {
            errorMessage =
                groupName +
                " points must contain exactly 4 objects.";

            return false;
        }

        for (int i = 0;
             i < PointCount;
             i++)
        {
            if (markers[i] == null)
            {
                errorMessage =
                    "Marker " +
                    pointPrefix +
                    (i + 1) +
                    " is not assigned in the Inspector.";

                return false;
            }

            points.Add(
                new WorldPointData
                {
                    name =
                        pointPrefix +
                        (i + 1),

                    worldPosition =
                        markers[i].position
                });
        }

        return true;
    }

    // =========================================================
    // Enter: send all 16 rays and all 12 reference-point positions
    // =========================================================

    private async void SendAllData()
    {
        if (!IsSessionComplete())
        {
            statusMessage =
                "Collection incomplete: " +
                GetTotalRayCount() +
                "/16 rays.";

            return;
        }

        if (!TryUpdateBoneWorldPoints(
                out string markerError))
        {
            statusMessage = markerError;

            Debug.LogError(markerError);

            return;
        }

        if (string.IsNullOrWhiteSpace(windowsIp))
        {
            statusMessage =
                "Windows IP address is empty.";

            return;
        }

        if (windowsPort < 1 ||
            windowsPort > 65535)
        {
            statusMessage =
                "Windows port is invalid.";

            return;
        }

        isSending = true;

        statusMessage =
            "Sending 16 rays and 12 reference-point positions...";

        /*
         * Compact single-line JSON.
         * The final newline marks the end of the message.
         */
        string json =
            JsonUtility.ToJson(
                session,
                false);

        byte[] data =
            Encoding.UTF8.GetBytes(
                json + "\n");

        Debug.Log(
            "JSON prepared for sending:\n" +
            json);

        try
        {
            using (TcpClient client =
                   new TcpClient())
            {
                client.NoDelay = true;

                Task connectTask =
                    client.ConnectAsync(
                        windowsIp,
                        windowsPort);

                Task completedTask =
                    await Task.WhenAny(
                        connectTask,
                        Task.Delay(5000));

                if (completedTask != connectTask)
                {
                    throw new TimeoutException(
                        "Connection timed out.");
                }

                await connectTask;

                using (NetworkStream stream =
                       client.GetStream())
                {
                    await stream.WriteAsync(
                        data,
                        0,
                        data.Length);

                    await stream.FlushAsync();
                }
            }

            statusMessage =
                "Data sent. " +
                "Press Enter again to resend.";
        }
        catch (Exception exception)
        {
            statusMessage =
                "Send failed: " +
                exception.Message +
                " Press Enter to retry.";

            Debug.LogError(
                "TCP send failed:\n" +
                exception);
        }
        finally
        {
            isSending = false;
        }
    }

    // =========================================================
    // Validation
    // =========================================================

    private bool IsSessionComplete()
    {
        if (session == null ||
            session.groups == null ||
            session.groups.Count != PointCount)
        {
            return false;
        }

        for (int i = 0;
             i < session.groups.Count;
             i++)
        {
            if (session.groups[i].rays == null ||
                session.groups[i].rays.Count !=
                RaysPerPoint)
            {
                return false;
            }
        }

        return true;
    }

    private int GetTotalRayCount()
    {
        if (session == null ||
            session.groups == null)
        {
            return 0;
        }

        int total = 0;

        for (int i = 0;
             i < session.groups.Count;
             i++)
        {
            if (session.groups[i].rays != null)
            {
                total +=
                    session.groups[i].rays.Count;
            }
        }

        return total;
    }

    // =========================================================
    // Draw captured rays in world space
    // =========================================================

    private GameObject CreateRayLine(
        Vector3 pointA,
        Vector3 pointB,
        int pointIndex,
        int rayIndex)
    {
        GameObject lineObject =
            new GameObject(
                "Point_" +
                (pointIndex + 1) +
                "_Ray_" +
                (rayIndex + 1));

        lineObject.transform.SetParent(
            rayContainer,
            false);

        LineRenderer lineRenderer =
            lineObject.AddComponent<LineRenderer>();

        lineRenderer.useWorldSpace = true;
        lineRenderer.positionCount = 2;

        lineRenderer.SetPosition(
            0,
            pointA);

        lineRenderer.SetPosition(
            1,
            pointB);

        lineRenderer.startWidth =
            lineWidth;

        lineRenderer.endWidth =
            lineWidth;

        lineRenderer.numCapVertices = 4;

        if (lineMaterial != null)
        {
            lineRenderer.sharedMaterial =
                lineMaterial;
        }

        Color color =
            GetPointColor(pointIndex);

        lineRenderer.startColor = color;
        lineRenderer.endColor = color;

        return lineObject;
    }

    private Color GetPointColor(
        int pointIndex)
    {
        switch (pointIndex)
        {
            case 0:
                return Color.red;

            case 1:
                return Color.green;

            case 2:
                return Color.blue;

            case 3:
                return Color.yellow;

            default:
                return Color.white;
        }
    }

    // =========================================================
    // English status panel in the Unity Game window
    // =========================================================

    private void OnGUI()
    {
        GUIStyle boxStyle =
            new GUIStyle(GUI.skin.box)
            {
                alignment =
                    TextAnchor.UpperLeft,

                padding =
                    new RectOffset(
                        18,
                        18,
                        14,
                        14)
            };

        GUIStyle titleStyle =
            new GUIStyle(GUI.skin.label)
            {
                fontSize = 26,
                fontStyle = FontStyle.Bold
            };

        GUIStyle normalStyle =
            new GUIStyle(GUI.skin.label)
            {
                fontSize = 20,
                wordWrap = true
            };

        GUILayout.BeginArea(
            new Rect(
                20,
                20,
                550,
                400),
            GUIContent.none,
            boxStyle);

        GUILayout.Label(
            "AR Ray Collection",
            titleStyle);

        GUILayout.Space(8);

        int currentRayCount = 0;

        if (session != null &&
            session.groups != null &&
            currentPointIndex >= 0 &&
            currentPointIndex <
            session.groups.Count)
        {
            currentRayCount =
                session.groups[currentPointIndex]
                    .rays.Count;
        }

        GUILayout.Label(
            "Current Point: " +
            (currentPointIndex + 1) +
            " / 4",
            normalStyle);

        GUILayout.Label(
            "Current Rays: " +
            currentRayCount +
            " / 4",
            normalStyle);

        GUILayout.Label(
            "Total Progress: " +
            GetTotalRayCount() +
            " / 16",
            normalStyle);

        GUILayout.Label(
            "Point Progress: " +
            CreateProgressText(
                currentRayCount),
            normalStyle);

        GUILayout.Space(12);

        GUILayout.Label(
            "Space (release): Capture Ray",
            normalStyle);

        GUILayout.Label(
            "Backspace: Undo Last Ray",
            normalStyle);

        GUILayout.Label(
            "Delete: Clear All Data",
            normalStyle);

        GUILayout.Label(
            "Enter: Send All Data",
            normalStyle);

        GUILayout.Space(12);

        GUILayout.Label(
            "Status: " +
            statusMessage,
            normalStyle);

        GUILayout.EndArea();
    }

    private string CreateProgressText(
        int capturedRayCount)
    {
        string result = "";

        for (int i = 0;
             i < RaysPerPoint;
             i++)
        {
            result +=
                i < capturedRayCount
                    ? "[X] "
                    : "[ ] ";
        }

        return result;
    }
}

// =============================================================
// Data sent to Windows
// No timestamps
// No fitting calculations
// =============================================================

[Serializable]
public class SessionData
{
    public List<PointGroup> groups;
    public BoneWorldPointData bonePoints;
}

[Serializable]
public class BoneWorldPointData
{
    public List<WorldPointData> femur;
    public List<WorldPointData> tibia;
    public List<WorldPointData> marker;
}

[Serializable]
public class WorldPointData
{
    public string name;
    public Vector3 worldPosition;
}

[Serializable]
public class PointGroup
{
    public int pointIndex;
    public List<RayData> rays;
}

[Serializable]
public class RayData
{
    public Vector3 pointA;
    public Vector3 pointB;
}