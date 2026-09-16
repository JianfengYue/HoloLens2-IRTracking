using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Microsoft.MixedReality.Toolkit.Input;
using Microsoft.MixedReality.Toolkit.Utilities;

/// <summary>
/// AR physical-point measurement in two stages:
/// 1. Coarse estimate from 2-4 MRTK right-hand rays.
/// 2. Fine estimate from four editable planes whose normals are the
///    vertices of a regular tetrahedron around the coarse estimate.
///
/// Unity world positions are in metres. Every value sent over TCP is in mm.
/// Attach this component to one scene object, then assign Virtual Point V and
/// Fitted Point Object (the existing small ball) in the Inspector.
///
/// Both the coarse and fine results report measurementDurationMs, the
/// elapsed time in milliseconds from capturing Ray 1 to that result: the
/// coarse value is recomputed on every coarse send, the fine value is
/// captured once, when the fine point is calculated.
/// </summary>
public class ARPointMeasurementManager : MonoBehaviour
{
    private const int MaximumRayCount = 4;
    private const int SphereCount = 4;
    private const float MetresToMillimetres = 1000.0f;
    private const float MillimetresToMetres = 0.001f;

    private enum MeasurementStage
    {
        CoarseRayCapture,
        FineSphereEditing,
        FineResultShown
    }

    [Header("Scene References")]
    [Tooltip("The known virtual point v. Its world position is used.")]
    public Transform virtualPointV;

    [Tooltip("An existing Unity object (the small ball) that will be moved to q.")]
    public GameObject fittedPointObject;

    [Tooltip("Hide the fitted-point object until at least two rays can be fitted.")]
    public bool hideFittedPointUntilAvailable = true;

    [Header("Ray Display")]
    [Min(0.1f)]
    public float rayLength = 2.0f;

    [Min(0.001f)]
    public float rayLineWidth = 0.001f;

    public Color rayColor = Color.cyan;

    [Header("Fine Estimate Geometry (mm)")]
    [Tooltip("Minimum permitted signed plane offset from q0 along its normal.")]
    public float minimumPlaneOffsetMm = -500.0f;

    [Tooltip("Maximum permitted signed plane offset from q0 along its normal.")]
    public float maximumPlaneOffsetMm = 500.0f;

    [Header("Fine Plane Display")]
    [Tooltip("Optional material for the plane meshes. Leave empty to use the generated transparent material.")]
    public Material planeMeshMaterialOverride;

    [Tooltip("Visual square plane side length.")]
    [Min(1.0f)]
    public float finePlaneSizeMm = 20.0f;

    [Tooltip("Visual plane thickness in Unity metres. 0.0005 is 0.5 mm.")]
    [Min(0.00001f)]
    public float finePlaneThicknessMetres = 0.0005f;

    [Range(0.02f, 1.0f)]
    public float planeOpacity = 0.35f;

    [Range(0.02f, 1.0f)]
    public float activePlaneOpacity = 0.65f;

    [Tooltip("Seconds each fine plane remains visible or hidden while editing.")]
    [Min(0.05f)]
    public float finePlaneBlinkSeconds = 1.0f;

    public Color activePlaneColor = Color.yellow;
    public Color plane1Color = Color.red;
    public Color plane2Color = Color.green;
    public Color plane3Color = Color.blue;
    public Color plane4Color = Color.magenta;

    [Header("Windows TCP Receiver")]
    public string windowsIp = "192.168.1.100";

    [Range(1, 65535)]
    public int windowsPort = 5000;

    [Min(0.1f)]
    public float connectionTimeoutSeconds = 5.0f;

    private MeasurementStage stage = MeasurementStage.CoarseRayCapture;

    private readonly List<ARMeasurementRayData> rays =
        new List<ARMeasurementRayData>();
    private readonly List<GameObject> rayLineObjects = new List<GameObject>();
    private readonly FineSphereState[] spheres = new FineSphereState[SphereCount];

    private Transform rayContainer;
    private Transform sphereContainer;
    private Material lineMaterial;
    private Material sphereMeshMaterial;
    private bool ownsSphereMeshMaterial;

    private Vector3 coarsePointQ0;
    private Vector3 finePointQ;
    private bool hasCoarsePoint;
    private bool hasFinePoint;
    private int activeSphereIndex = -1;
    private bool finePlaneBlinkVisible = true;
    private float nextFinePlaneBlinkTime;

    // Time.realtimeSinceStartup when Ray 1 was captured; -1 means no ray
    // has been captured yet for the current measurement.
    private float firstRayRealtime = -1.0f;
    private float finePointMeasurementDurationMs;

    private string statusMessage = "Ready.";
    private string networkMessage = "No data sent yet.";

    private readonly SemaphoreSlim sendSemaphore = new SemaphoreSlim(1, 1);
    private bool isDestroyed;

    private void Start()
    {
        CreateRuntimeObjects();
        ResetMeasurement();
    }

    private void Update()
    {
        HandleNumberKeys();

        // MRTK Editor input simulation uses Space to manipulate the simulated
        // right hand, so capture/confirm on key release, as in the old script.
        if (Input.GetKeyUp(KeyCode.Space))
        {
            HandleSpaceReleased();
        }

        if (Input.GetKeyDown(KeyCode.Backspace))
        {
            HandleBackspace();
        }

        if (Input.GetKeyDown(KeyCode.Delete))
        {
            HandleDelete();
        }

        if (Input.GetKeyDown(KeyCode.Return) ||
            Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            HandleEnter();
        }

        if (stage == MeasurementStage.FineSphereEditing)
        {
            HandleRadiusKeys();
            UpdateFinePlaneBlink();
        }
    }

    private void OnDestroy()
    {
        isDestroyed = true;

        if (lineMaterial != null)
        {
            Destroy(lineMaterial);
        }

        if (ownsSphereMeshMaterial && sphereMeshMaterial != null)
        {
            Destroy(sphereMeshMaterial);
        }

        sendSemaphore.Dispose();
    }

    // =====================================================================
    // State-machine input
    // =====================================================================

    private void HandleSpaceReleased()
    {
        switch (stage)
        {
            case MeasurementStage.CoarseRayCapture:
                if (rays.Count < MaximumRayCount)
                {
                    CaptureRay();
                }
                else
                {
                    BeginFineEstimate("Four rays retained. Fine estimate started.");
                }
                break;

            case MeasurementStage.FineSphereEditing:
                ConfirmActiveSphereAndAdvance();
                break;

            case MeasurementStage.FineResultShown:
                statusMessage =
                    "Fine result is displayed. Press Delete to start a new measurement.";
                break;
        }
    }

    private void HandleBackspace()
    {
        if (stage != MeasurementStage.CoarseRayCapture)
        {
            statusMessage = "Backspace is only available during coarse ray capture.";
            return;
        }

        UndoLastRay();
    }

    private void HandleDelete()
    {
        ResetMeasurement();
        statusMessage = "Measurement cleared. Ready for a new measurement.";
    }

    private void HandleEnter()
    {
        switch (stage)
        {
            case MeasurementStage.CoarseRayCapture:
                SendCoarseResultAndBeginFineEstimate();
                break;

            case MeasurementStage.FineSphereEditing:
                if (!AreAllSpheresConfirmed())
                {
                    statusMessage =
                        "Enter is not available until all four planes are confirmed.";
                    return;
                }

                CalculateShowAndSendFineResult();
                break;

            case MeasurementStage.FineResultShown:
                // A second Enter is a convenient explicit resend and does not
                // change the already displayed result.
                SendFineResult();
                statusMessage = "Fine result queued for resend.";
                break;
        }
    }

    private void HandleNumberKeys()
    {
        if (stage != MeasurementStage.FineSphereEditing)
        {
            return;
        }

        if (Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1))
        {
            SelectSphereFromNumberKey(0);
        }
        else if (Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2))
        {
            SelectSphereFromNumberKey(1);
        }
        else if (Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3))
        {
            SelectSphereFromNumberKey(2);
        }
        else if (Input.GetKeyDown(KeyCode.Alpha4) || Input.GetKeyDown(KeyCode.Keypad4))
        {
            SelectSphereFromNumberKey(3);
        }
    }

    private void HandleRadiusKeys()
    {
        if (activeSphereIndex < 0 || activeSphereIndex >= SphereCount)
        {
            return;
        }

        float deltaMm = 0.0f;

        if (Input.GetKeyDown(KeyCode.LeftArrow))
        {
            deltaMm = -1.0f;
        }
        else if (Input.GetKeyDown(KeyCode.RightArrow))
        {
            deltaMm = 1.0f;
        }
        else if (Input.GetKeyDown(KeyCode.UpArrow))
        {
            deltaMm = 0.1f;
        }
        else if (Input.GetKeyDown(KeyCode.DownArrow))
        {
            deltaMm = -0.1f;
        }

        if (Mathf.Approximately(deltaMm, 0.0f))
        {
            return;
        }

        FineSphereState plane = spheres[activeSphereIndex];
        float newOffsetMm = Mathf.Clamp(
            plane.offsetMetres * MetresToMillimetres + deltaMm,
            minimumPlaneOffsetMm,
            maximumPlaneOffsetMm);

        plane.offsetMetres = newOffsetMm * MillimetresToMetres;
        plane.centerWorld = coarsePointQ0 + plane.normalWorld * plane.offsetMetres;
        plane.confirmed = false;
        ResetFinePlaneBlink();
        UpdateSphereGeometry(plane);
        RefreshSphereVisibilityAndStyle();

        statusMessage =
            "Plane " + (activeSphereIndex + 1) +
            " offset: " + newOffsetMm.ToString("F1") + " mm.";
    }

    // =====================================================================
    // Coarse estimate: right-hand ray capture and least-squares line fitting
    // =====================================================================

    private void CaptureRay()
    {
        if (rays.Count >= MaximumRayCount)
        {
            statusMessage = "Four rays already exist. Release Space again for fine estimate.";
            return;
        }

        if (!TryGetRightHandRay(out Vector3 pointA, out Vector3 pointB))
        {
            statusMessage =
                "Right-hand ray not found. Show the right hand and try again.";
            Debug.LogWarning("No active MRTK right-hand LinePointer was found.");
            return;
        }

        ARMeasurementRayData ray = new ARMeasurementRayData
        {
            pointA = pointA,
            pointB = pointB
        };

        if (rays.Count == 0)
        {
            firstRayRealtime = Time.realtimeSinceStartup;
        }

        rays.Add(ray);
        rayLineObjects.Add(CreateRayLine(pointA, pointB, rays.Count - 1));

        UpdateCoarseFit();

        bool coarseResultQueued =
            rays.Count >= 2 &&
            SendCoarseResult("ray " + rays.Count + " coarse result");

        if (rays.Count == MaximumRayCount)
        {
            if (coarseResultQueued)
            {
                statusMessage =
                    "Ray 4 captured and residual queued. Release Space again for fine estimate, " +
                    "or press Backspace to replace Ray 4.";
            }
        }
        else if (rays.Count == 1)
        {
            statusMessage = "Ray 1 captured. One ray cannot define q.";
        }
        else if (coarseResultQueued)
        {
            statusMessage =
                "Ray " + rays.Count + " captured. Coarse residual queued.";
        }

        Debug.Log(
            "Captured Ray " + rays.Count +
            "\nPoint A: " + pointA +
            "\nPoint B: " + pointB);
    }

    private bool TryGetRightHandRay(out Vector3 pointA, out Vector3 pointB)
    {
        pointA = Vector3.zero;
        pointB = Vector3.zero;

        foreach (LinePointer pointer in PointerUtils.GetPointers<LinePointer>(
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

            Vector3 direction = pointer.Rays[0].Direction;

            if (direction.sqrMagnitude < 0.000001f)
            {
                continue;
            }

            pointA = pointer.Rays[0].Origin;
            pointB = pointA + direction.normalized * rayLength;
            return true;
        }

        return false;
    }

    private void UndoLastRay()
    {
        if (rays.Count == 0)
        {
            statusMessage = "There is no ray to remove.";
            return;
        }

        int lastIndex = rays.Count - 1;
        rays.RemoveAt(lastIndex);

        if (rays.Count == 0)
        {
            firstRayRealtime = -1.0f;
        }

        if (lastIndex < rayLineObjects.Count)
        {
            GameObject line = rayLineObjects[lastIndex];
            rayLineObjects.RemoveAt(lastIndex);

            if (line != null)
            {
                Destroy(line);
            }
        }

        UpdateCoarseFit();
        statusMessage =
            "Last ray removed. " + rays.Count + "/4 rays remain. " +
            "The next Space release captures the missing ray.";
    }

    /// <summary>
    /// Minimizes the sum of squared distances from q to the captured half-rays.
    /// The closest point is allowed only at a + t*d with t >= 0. Because there
    /// are at most four rays, all forward/endpoint regions can be enumerated
    /// exactly and the best valid least-squares solution selected.
    /// </summary>
    private bool TryFitPointToRays(out Vector3 fittedPoint)
    {
        fittedPoint = Vector3.zero;

        if (rays.Count < 2)
        {
            return false;
        }

        Vector3[] origins = new Vector3[rays.Count];
        Vector3[] directions = new Vector3[rays.Count];

        for (int i = 0; i < rays.Count; i++)
        {
            Vector3 rawDirection = rays[i].pointB - rays[i].pointA;

            if (rawDirection.sqrMagnitude < 0.0000000001f)
            {
                return false;
            }

            origins[i] = rays[i].pointA;
            directions[i] = rawDirection.normalized;
        }

        bool found = false;
        double bestSquaredError = double.PositiveInfinity;
        int regionCount = 1 << rays.Count;

        for (int regionMask = 0; regionMask < regionCount; regionMask++)
        {
            double[,] matrix = new double[3, 3];
            double[] rightSide = new double[3];

            for (int i = 0; i < rays.Count; i++)
            {
                Vector3 d = directions[i];
                Vector3 a = origins[i];
                bool usesForwardRay = (regionMask & (1 << i)) != 0;

                // Forward region: distance to the infinite supporting line.
                // Endpoint region: distance to the ray origin (identity).
                double[,] weight = usesForwardRay
                    ? new double[,]
                    {
                        { 1.0 - d.x * d.x, -d.x * d.y,       -d.x * d.z       },
                        { -d.y * d.x,      1.0 - d.y * d.y,  -d.y * d.z       },
                        { -d.z * d.x,      -d.z * d.y,       1.0 - d.z * d.z  }
                    }
                    : new double[,]
                    {
                        { 1.0, 0.0, 0.0 },
                        { 0.0, 1.0, 0.0 },
                        { 0.0, 0.0, 1.0 }
                    };

                for (int row = 0; row < 3; row++)
                {
                    for (int column = 0; column < 3; column++)
                    {
                        matrix[row, column] += weight[row, column];
                    }

                    rightSide[row] +=
                        weight[row, 0] * a.x +
                        weight[row, 1] * a.y +
                        weight[row, 2] * a.z;
                }
            }

            if (!TrySolve3By3(matrix, rightSide, out double[] solution))
            {
                continue;
            }

            Vector3 candidate = new Vector3(
                (float)solution[0],
                (float)solution[1],
                (float)solution[2]);

            if (!IsFinite(candidate))
            {
                continue;
            }

            bool belongsToRegion = true;

            for (int i = 0; i < rays.Count; i++)
            {
                float projection =
                    Vector3.Dot(directions[i], candidate - origins[i]);
                bool usesForwardRay = (regionMask & (1 << i)) != 0;

                if ((usesForwardRay && projection < -0.000001f) ||
                    (!usesForwardRay && projection > 0.000001f))
                {
                    belongsToRegion = false;
                    break;
                }
            }

            if (!belongsToRegion)
            {
                continue;
            }

            double squaredError = 0.0;

            for (int i = 0; i < rays.Count; i++)
            {
                float projection = Mathf.Max(
                    0.0f,
                    Vector3.Dot(directions[i], candidate - origins[i]));
                Vector3 nearestPoint = origins[i] + directions[i] * projection;
                squaredError += (candidate - nearestPoint).sqrMagnitude;
            }

            if (squaredError < bestSquaredError)
            {
                bestSquaredError = squaredError;
                fittedPoint = candidate;
                found = true;
            }
        }

        return found;
    }

    private void UpdateCoarseFit()
    {
        hasCoarsePoint = TryFitPointToRays(out coarsePointQ0);

        if (hasCoarsePoint)
        {
            MoveFittedPointObject(coarsePointQ0, true);
        }
        else if (hideFittedPointUntilAvailable && fittedPointObject != null)
        {
            fittedPointObject.SetActive(false);
        }
    }

    private void SendCoarseResultAndBeginFineEstimate()
    {
        if (rays.Count < 2)
        {
            statusMessage = "At least two rays are required before Enter can be used.";
            return;
        }

        if (!UpdateAndValidateCoarsePoint())
        {
            return;
        }

        if (!TryGetVirtualPoint(out Vector3 pointV))
        {
            return;
        }

        if (!ValidateNetworkSettings())
        {
            statusMessage = "Coarse result was not sent. Check the TCP settings.";
            return;
        }

        SendCoarseResult("coarse result");
        BeginFineEstimate("Coarse residual queued. Fine estimate started.");
    }

    private bool SendCoarseResult(string description)
    {
        if (rays.Count < 2)
        {
            statusMessage = "At least two rays are required before a coarse result can be sent.";
            return false;
        }

        if (!UpdateAndValidateCoarsePoint())
        {
            return false;
        }

        if (!TryGetVirtualPoint(out Vector3 pointV))
        {
            return false;
        }

        if (!ValidateNetworkSettings())
        {
            statusMessage = "Coarse result was not sent. Check the TCP settings.";
            return false;
        }

        CoarseResultMessage message = new CoarseResultMessage
        {
            messageType = "coarse_result",
            unit = "mm",
            rayCount = rays.Count,
            qMm = ToMillimetres(coarsePointQ0),
            vMm = ToMillimetres(pointV),
            absoluteResidualMm =
                Vector3.Distance(coarsePointQ0, pointV) * MetresToMillimetres,
            measurementDurationMs = GetElapsedMeasurementMs()
        };

        QueueTcpMessage(message, description);
        return true;
    }

    private bool UpdateAndValidateCoarsePoint()
    {
        UpdateCoarseFit();

        if (hasCoarsePoint)
        {
            return true;
        }

        statusMessage =
            "The rays are parallel or numerically degenerate; q cannot be fitted.";
        return false;
    }

    // =====================================================================
    // Fine estimate: regular tetrahedron and four editable thin planes
    // =====================================================================

    private void BeginFineEstimate(string message)
    {
        if (!UpdateAndValidateCoarsePoint())
        {
            return;
        }

        DestroyFineSphereObjects();

        // These four unit vectors form a regular tetrahedron centered at zero.
        Vector3[] directions =
        {
            new Vector3( 1.0f,  1.0f,  1.0f).normalized,
            new Vector3( 1.0f, -1.0f, -1.0f).normalized,
            new Vector3(-1.0f,  1.0f, -1.0f).normalized,
            new Vector3(-1.0f, -1.0f,  1.0f).normalized
        };

        for (int i = 0; i < SphereCount; i++)
        {
            FineSphereState sphere = new FineSphereState
            {
                index = i,
                normalWorld = directions[i],
                offsetMetres = 0.0f,
                centerWorld = coarsePointQ0,
                confirmed = false
            };

            sphere.root = CreateMeshSphere(sphere);
            spheres[i] = sphere;
        }

        stage = MeasurementStage.FineSphereEditing;
        hasFinePoint = false;
        activeSphereIndex = 0;
        ResetFinePlaneBlink();
        UpdateSphereGeometry(spheres[activeSphereIndex]);

        // Coarse rays are retained in memory for the result, but are hidden
        // throughout the fine-estimate stage so they do not obstruct the mesh.
        if (rayContainer != null)
        {
            rayContainer.gameObject.SetActive(false);
        }

        // Hide the coarse fitted-point ball during plane editing so it does
        // not compete visually with the real physical ball being aligned.
        HideFittedPointObject();

        RefreshSphereVisibilityAndStyle();

        statusMessage = message + " Plane 1 is active.";
    }

    private void SelectSphereFromNumberKey(int targetIndex)
    {
        if (targetIndex < 0 || targetIndex >= SphereCount)
        {
            return;
        }

        if (targetIndex == activeSphereIndex)
        {
            statusMessage = "Plane " + (targetIndex + 1) + " is already active.";
            return;
        }

        // Per requirement, changing to another plane automatically confirms
        // the plane that was being edited and sends its result.
        if (activeSphereIndex >= 0)
        {
            if (!ConfirmSphere(activeSphereIndex, "number-key switch"))
            {
                return;
            }
        }

        // Selecting a previously confirmed plane opens it for re-editing; it
        // must therefore be confirmed again before the final Enter is valid.
        activeSphereIndex = targetIndex;
        spheres[targetIndex].confirmed = false;
        ResetFinePlaneBlink();
        UpdateSphereGeometry(spheres[activeSphereIndex]);
        RefreshSphereVisibilityAndStyle();

        statusMessage =
            "Plane " + (targetIndex + 1) + " selected for editing.";
    }

    private void ConfirmActiveSphereAndAdvance()
    {
        if (activeSphereIndex < 0 || activeSphereIndex >= SphereCount)
        {
            statusMessage =
                AreAllSpheresConfirmed()
                    ? "All planes are confirmed. Press Enter."
                    : "Select an unconfirmed plane with keys 1-4.";
            return;
        }

        int justConfirmed = activeSphereIndex;
        if (!ConfirmSphere(justConfirmed, "Space"))
        {
            return;
        }

        int nextIndex = FindNextUnconfirmedSphere(justConfirmed);

        if (nextIndex >= 0)
        {
            activeSphereIndex = nextIndex;
            ResetFinePlaneBlink();
            UpdateSphereGeometry(spheres[activeSphereIndex]);
            statusMessage =
                "Plane " + (justConfirmed + 1) +
                " confirmed. Plane " + (nextIndex + 1) + " is active.";
        }
        else
        {
            activeSphereIndex = -1;
            statusMessage =
                "All four planes are confirmed. Press Enter to calculate q.";
        }

        RefreshSphereVisibilityAndStyle();
    }

    private bool ConfirmSphere(int sphereIndex, string confirmationSource)
    {
        FineSphereState sphere = spheres[sphereIndex];

        if (!TryGetVirtualPoint(out Vector3 pointV))
        {
            statusMessage =
                "Plane was not confirmed because Virtual Point V is not assigned.";
            return false;
        }

        if (!ValidateNetworkSettings())
        {
            statusMessage =
                "Plane was not confirmed because the TCP settings are invalid.";
            return false;
        }

        float signedResidualMm =
            Vector3.Dot(pointV - sphere.centerWorld, sphere.normalWorld) *
            MetresToMillimetres;

        sphere.confirmed = true;

        float centerToVMetres = Vector3.Distance(sphere.centerWorld, pointV);
        SphereConfirmationMessage message = new SphereConfirmationMessage
        {
            messageType = "plane_confirmation",
            unit = "mm",
            sphereIndex = sphereIndex + 1,
            confirmationSource = confirmationSource,
            centerMm = ToMillimetres(sphere.centerWorld),
            normal = sphere.normalWorld,
            vMm = ToMillimetres(pointV),
            radiusMm = sphere.offsetMetres * MetresToMillimetres,
            centerToVMm = centerToVMetres * MetresToMillimetres,
            // Positive: v is in front of the plane along its normal.
            signedSurfaceResidualMm = signedResidualMm,
            absoluteSurfaceResidualMm = Mathf.Abs(signedResidualMm)
        };

        QueueTcpMessage(message, "plane " + (sphereIndex + 1) + " result");
        return true;
    }

    private int FindNextUnconfirmedSphere(int afterIndex)
    {
        for (int offset = 1; offset <= SphereCount; offset++)
        {
            int index = (afterIndex + offset) % SphereCount;

            if (!spheres[index].confirmed)
            {
                return index;
            }
        }

        return -1;
    }

    private bool AreAllSpheresConfirmed()
    {
        for (int i = 0; i < SphereCount; i++)
        {
            if (spheres[i] == null || !spheres[i].confirmed)
            {
                return false;
            }
        }

        return true;
    }

    private void CalculateShowAndSendFineResult()
    {
        if (!TryFitPointToSpheres(coarsePointQ0, out finePointQ))
        {
            statusMessage = "Fine plane fitting failed because the geometry is degenerate.";
            return;
        }

        hasFinePoint = true;
        stage = MeasurementStage.FineResultShown;
        activeSphereIndex = -1;
        finePlaneBlinkVisible = true;

        finePointMeasurementDurationMs = GetElapsedMeasurementMs();

        MoveFittedPointObject(finePointQ, true);
        RefreshSphereVisibilityAndStyle();
        SendFineResult();

        statusMessage =
            "Fine q calculated. All planes are shown. Press Delete to clear.";
    }

    /// <summary>
    /// Least-squares intersection of the four confirmed planes:
    /// dot(normal_i, q) = dot(normal_i, planePoint_i).
    /// </summary>
    private bool TryFitPointToSpheres(Vector3 initialPoint, out Vector3 fittedPoint)
    {
        fittedPoint = initialPoint;
        double[,] normalMatrix = new double[3, 3];
        double[] rightSide = new double[3];

        for (int i = 0; i < SphereCount; i++)
        {
            if (spheres[i] == null)
            {
                return false;
            }

            Vector3 n = spheres[i].normalWorld.normalized;
            double[] values = { n.x, n.y, n.z };
            double offset =
                Vector3.Dot(n, spheres[i].centerWorld);

            for (int row = 0; row < 3; row++)
            {
                rightSide[row] += values[row] * offset;

                for (int column = 0; column < 3; column++)
                {
                    normalMatrix[row, column] +=
                        values[row] * values[column];
                }
            }
        }

        if (!TrySolve3By3(normalMatrix, rightSide, out double[] solution))
        {
            return false;
        }

        fittedPoint = new Vector3(
            (float)solution[0],
            (float)solution[1],
            (float)solution[2]);
        return IsFinite(fittedPoint);
    }

    private double CalculateSphereSquaredError(Vector3 point)
    {
        double sum = 0.0;

        for (int i = 0; i < SphereCount; i++)
        {
            double residual =
                Vector3.Dot(
                    spheres[i].normalWorld,
                    point - spheres[i].centerWorld);

            sum += residual * residual;
        }

        return sum;
    }

    private void SendFineResult()
    {
        if (!hasFinePoint)
        {
            statusMessage = "There is no fine result to send.";
            return;
        }

        if (!TryGetVirtualPoint(out Vector3 pointV))
        {
            return;
        }

        List<FineSphereReport> reports = new List<FineSphereReport>();

        for (int i = 0; i < SphereCount; i++)
        {
            float centerToV = Vector3.Distance(spheres[i].centerWorld, pointV);
            float signedResidual =
                Vector3.Dot(pointV - spheres[i].centerWorld, spheres[i].normalWorld);

            reports.Add(new FineSphereReport
            {
                sphereIndex = i + 1,
                centerMm = ToMillimetres(spheres[i].centerWorld),
                normal = spheres[i].normalWorld,
                radiusMm = spheres[i].offsetMetres * MetresToMillimetres,
                centerToVMm = centerToV * MetresToMillimetres,
                signedSurfaceResidualMm = signedResidual * MetresToMillimetres,
                absoluteSurfaceResidualMm =
                    Mathf.Abs(signedResidual) * MetresToMillimetres
            });
        }

        FineResultMessage message = new FineResultMessage
        {
            messageType = "fine_result",
            unit = "mm",
            q0Mm = ToMillimetres(coarsePointQ0),
            qMm = ToMillimetres(finePointQ),
            vMm = ToMillimetres(pointV),
            absoluteResidualMm =
                Vector3.Distance(finePointQ, pointV) * MetresToMillimetres,
            spheres = reports,
            planes = reports,
            measurementDurationMs = finePointMeasurementDurationMs
        };

        QueueTcpMessage(message, "fine result");
    }

    // =====================================================================
    // Runtime visualization
    // =====================================================================

    private void CreateRuntimeObjects()
    {
        rayContainer = new GameObject("CapturedRays").transform;
        rayContainer.SetParent(transform, false);

        sphereContainer = new GameObject("FineEstimateSpheres").transform;
        sphereContainer.SetParent(transform, false);

        Shader shader = Shader.Find("Sprites/Default");

        if (shader == null)
        {
            shader = Shader.Find("Unlit/Color");
        }

        if (shader != null)
        {
            lineMaterial = new Material(shader);
        }
        else
        {
            Debug.LogWarning("No line shader found. Lines may use Unity's error material.");
        }

        CreateSphereMeshMaterial();
    }

    private void CreateSphereMeshMaterial()
    {
        if (planeMeshMaterialOverride != null)
        {
            sphereMeshMaterial = planeMeshMaterialOverride;
            ownsSphereMeshMaterial = false;
            return;
        }

        Shader shader =
            UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null
                ? Shader.Find("Universal Render Pipeline/Lit")
                : Shader.Find("Standard");

        if (shader == null &&
            UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null)
        {
            shader = Shader.Find("Standard");
        }

        if (shader == null)
        {
            shader = Shader.Find("Sprites/Default");
        }

        if (shader == null)
        {
            shader = Shader.Find("Unlit/Color");
        }

        if (shader == null)
        {
            Debug.LogWarning(
                "No plane shader found. Mesh planes will use Unity's default material.");
            return;
        }

        sphereMeshMaterial = new Material(shader);
        sphereMeshMaterial.name = "GeneratedTransparentPlaneMaterial";
        ownsSphereMeshMaterial = true;

        Color initialColor = new Color(1.0f, 1.0f, 1.0f, planeOpacity);

        if (sphereMeshMaterial.HasProperty("_Color"))
        {
            sphereMeshMaterial.SetColor("_Color", initialColor);
        }

        if (sphereMeshMaterial.HasProperty("_BaseColor"))
        {
            sphereMeshMaterial.SetColor("_BaseColor", initialColor);
        }

        // Built-in Standard shader transparency.
        if (sphereMeshMaterial.HasProperty("_Mode"))
        {
            sphereMeshMaterial.SetFloat("_Mode", 3.0f);
        }

        // URP Lit shader transparency.
        if (sphereMeshMaterial.HasProperty("_Surface"))
        {
            sphereMeshMaterial.SetFloat("_Surface", 1.0f);
            sphereMeshMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        }

        if (sphereMeshMaterial.HasProperty("_SrcBlend"))
        {
            sphereMeshMaterial.SetInt(
                "_SrcBlend",
                (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        }

        if (sphereMeshMaterial.HasProperty("_DstBlend"))
        {
            sphereMeshMaterial.SetInt(
                "_DstBlend",
                (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        }

        if (sphereMeshMaterial.HasProperty("_ZWrite"))
        {
            sphereMeshMaterial.SetInt("_ZWrite", 0);
        }

        if (sphereMeshMaterial.HasProperty("_Cull"))
        {
            sphereMeshMaterial.SetInt(
                "_Cull",
                (int)UnityEngine.Rendering.CullMode.Off);
        }

        sphereMeshMaterial.DisableKeyword("_ALPHATEST_ON");
        sphereMeshMaterial.EnableKeyword("_ALPHABLEND_ON");
        sphereMeshMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        sphereMeshMaterial.renderQueue =
            (int)UnityEngine.Rendering.RenderQueue.Transparent;
    }

    private GameObject CreateRayLine(Vector3 pointA, Vector3 pointB, int rayIndex)
    {
        GameObject lineObject = new GameObject("Ray_" + (rayIndex + 1));
        lineObject.transform.SetParent(rayContainer, false);

        LineRenderer renderer = lineObject.AddComponent<LineRenderer>();
        renderer.useWorldSpace = true;
        renderer.positionCount = 2;
        renderer.SetPosition(0, pointA);
        renderer.SetPosition(1, pointB);
        renderer.startWidth = rayLineWidth;
        renderer.endWidth = rayLineWidth;
        renderer.numCapVertices = 4;
        renderer.startColor = rayColor;
        renderer.endColor = rayColor;

        if (lineMaterial != null)
        {
            renderer.sharedMaterial = lineMaterial;
        }

        return lineObject;
    }

    private GameObject CreateMeshSphere(FineSphereState sphere)
    {
        GameObject root = GameObject.CreatePrimitive(PrimitiveType.Cube);
        root.name = "FinePlane_" + (sphere.index + 1);
        root.transform.SetParent(sphereContainer, false);
        root.transform.position = sphere.centerWorld;
        root.transform.rotation =
            Quaternion.FromToRotation(Vector3.up, sphere.normalWorld.normalized);
        sphere.root = root;

        Collider sphereCollider = root.GetComponent<Collider>();

        if (sphereCollider != null)
        {
            Destroy(sphereCollider);
        }

        sphere.meshRenderer = root.GetComponent<MeshRenderer>();

        if (sphere.meshRenderer != null && sphereMeshMaterial != null)
        {
            sphere.meshRenderer.sharedMaterial = sphereMeshMaterial;
            sphere.meshRenderer.shadowCastingMode =
                UnityEngine.Rendering.ShadowCastingMode.Off;
            sphere.meshRenderer.receiveShadows = false;
        }

        UpdateSphereGeometry(sphere);
        return root;
    }

    private void UpdateSphereGeometry(FineSphereState sphere)
    {
        if (sphere == null || sphere.root == null)
        {
            return;
        }

        float sideLength = finePlaneSizeMm * MillimetresToMetres;
        sphere.root.transform.position = sphere.centerWorld;
        sphere.root.transform.rotation =
            Quaternion.FromToRotation(Vector3.up, sphere.normalWorld.normalized);
        sphere.root.transform.localScale =
            new Vector3(sideLength, finePlaneThicknessMetres, sideLength);
    }

    private void ResetFinePlaneBlink()
    {
        finePlaneBlinkVisible = true;
        nextFinePlaneBlinkTime =
            Time.time + Mathf.Max(0.05f, finePlaneBlinkSeconds);
    }

    private void UpdateFinePlaneBlink()
    {
        if (Time.time < nextFinePlaneBlinkTime)
        {
            return;
        }

        finePlaneBlinkVisible = !finePlaneBlinkVisible;
        nextFinePlaneBlinkTime =
            Time.time + Mathf.Max(0.05f, finePlaneBlinkSeconds);
        RefreshSphereVisibilityAndStyle();
    }

    private void RefreshSphereVisibilityAndStyle()
    {
        for (int i = 0; i < SphereCount; i++)
        {
            FineSphereState sphere = spheres[i];

            if (sphere == null || sphere.root == null)
            {
                continue;
            }

            bool visibleInStage =
                stage == MeasurementStage.FineResultShown ||
                (stage == MeasurementStage.FineSphereEditing &&
                 (i == activeSphereIndex || AreAllSpheresConfirmed()));
            bool visible =
                visibleInStage &&
                (stage != MeasurementStage.FineSphereEditing ||
                 finePlaneBlinkVisible);

            sphere.root.SetActive(visible);

            Color color =
                stage == MeasurementStage.FineSphereEditing && i == activeSphereIndex
                    ? activePlaneColor
                    : GetSphereColor(i);

            if (sphere.meshRenderer == null)
            {
                continue;
            }

            color.a =
                stage == MeasurementStage.FineSphereEditing && i == activeSphereIndex
                    ? activePlaneOpacity
                    : planeOpacity;

            MaterialPropertyBlock properties = new MaterialPropertyBlock();
            sphere.meshRenderer.GetPropertyBlock(properties);
            properties.SetColor("_Color", color);
            properties.SetColor("_BaseColor", color);
            sphere.meshRenderer.SetPropertyBlock(properties);
        }
    }

    private Color GetSphereColor(int index)
    {
        switch (index)
        {
            case 0: return plane1Color;
            case 1: return plane2Color;
            case 2: return plane3Color;
            case 3: return plane4Color;
            default: return Color.white;
        }
    }

    private void MoveFittedPointObject(Vector3 position, bool makeVisible)
    {
        if (fittedPointObject == null)
        {
            return;
        }

        fittedPointObject.transform.position = position;

        if (makeVisible && !fittedPointObject.activeSelf)
        {
            fittedPointObject.SetActive(true);
        }
    }

    private void HideFittedPointObject()
    {
        if (fittedPointObject != null && fittedPointObject.activeSelf)
        {
            fittedPointObject.SetActive(false);
        }
    }

    // =====================================================================
    // Reset and cleanup
    // =====================================================================

    private void ResetMeasurement()
    {
        DestroyAllRayObjects();
        DestroyFineSphereObjects();

        if (rayContainer != null)
        {
            rayContainer.gameObject.SetActive(true);
        }

        rays.Clear();
        stage = MeasurementStage.CoarseRayCapture;
        hasCoarsePoint = false;
        hasFinePoint = false;
        activeSphereIndex = -1;
        finePlaneBlinkVisible = true;
        nextFinePlaneBlinkTime = 0.0f;
        coarsePointQ0 = Vector3.zero;
        finePointQ = Vector3.zero;
        firstRayRealtime = -1.0f;
        finePointMeasurementDurationMs = 0.0f;

        if (hideFittedPointUntilAvailable && fittedPointObject != null)
        {
            fittedPointObject.SetActive(false);
        }

        statusMessage = "Release Space to capture Ray 1.";
    }

    private void DestroyAllRayObjects()
    {
        for (int i = 0; i < rayLineObjects.Count; i++)
        {
            if (rayLineObjects[i] != null)
            {
                Destroy(rayLineObjects[i]);
            }
        }

        rayLineObjects.Clear();
    }

    private void DestroyFineSphereObjects()
    {
        for (int i = 0; i < SphereCount; i++)
        {
            if (spheres[i] != null && spheres[i].root != null)
            {
                Destroy(spheres[i].root);
            }

            spheres[i] = null;
        }

        activeSphereIndex = -1;
    }

    // =====================================================================
    // TCP JSON sending
    // =====================================================================

    private async void QueueTcpMessage(object payload, string description)
    {
        if (!ValidateNetworkSettings())
        {
            return;
        }

        string json = JsonUtility.ToJson(payload, false);
        networkMessage = "Queued " + description + ".";

        try
        {
            await sendSemaphore.WaitAsync();

            if (isDestroyed)
            {
                return;
            }

            networkMessage = "Sending " + description + "...";
            byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");

            using (TcpClient client = new TcpClient())
            {
                client.NoDelay = true;

                Task connectTask = client.ConnectAsync(windowsIp, windowsPort);
                Task completedTask = await Task.WhenAny(
                    connectTask,
                    Task.Delay(Mathf.Max(100, (int)(connectionTimeoutSeconds * 1000.0f))));

                if (completedTask != connectTask)
                {
                    throw new TimeoutException("Connection timed out.");
                }

                await connectTask;

                using (NetworkStream stream = client.GetStream())
                {
                    await stream.WriteAsync(bytes, 0, bytes.Length);
                    await stream.FlushAsync();
                }
            }

            networkMessage = "Sent " + description + ".";
            Debug.Log("TCP JSON sent:\n" + json);
        }
        catch (ObjectDisposedException)
        {
            // The Unity object was destroyed while a queued send was waiting.
        }
        catch (Exception exception)
        {
            if (!isDestroyed)
            {
                networkMessage = "Send failed: " + exception.Message;
                Debug.LogError("TCP send failed:\n" + exception);
            }
        }
        finally
        {
            if (!isDestroyed)
            {
                sendSemaphore.Release();
            }
        }
    }

    private bool ValidateNetworkSettings()
    {
        if (string.IsNullOrWhiteSpace(windowsIp))
        {
            networkMessage = "Send failed: Windows IP is empty.";
            return false;
        }

        if (windowsPort < 1 || windowsPort > 65535)
        {
            networkMessage = "Send failed: Windows port is invalid.";
            return false;
        }

        return true;
    }

    // =====================================================================
    // Math and data helpers
    // =====================================================================

    private bool TryGetVirtualPoint(out Vector3 pointV)
    {
        pointV = Vector3.zero;

        if (virtualPointV == null)
        {
            statusMessage = "Virtual Point V is not assigned in the Inspector.";
            return false;
        }

        pointV = virtualPointV.position;
        return true;
    }

    // Elapsed time from Ray 1 to right now, in milliseconds. Used for both
    // the coarse result (recomputed on every send) and the fine result
    // (captured once, when the fine point is calculated).
    private float GetElapsedMeasurementMs()
    {
        return firstRayRealtime >= 0.0f
            ? (Time.realtimeSinceStartup - firstRayRealtime) * 1000.0f
            : 0.0f;
    }

    private static Vector3 ToMillimetres(Vector3 metres)
    {
        return metres * MetresToMillimetres;
    }

    private static bool IsFinite(Vector3 value)
    {
        return
            !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
            !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }

    private static bool TrySolve3By3(
        double[,] matrix,
        double[] rightSide,
        out double[] solution)
    {
        solution = new double[3];
        double[,] augmented = new double[3, 4];

        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                augmented[row, column] = matrix[row, column];
            }

            augmented[row, 3] = rightSide[row];
        }

        for (int pivotColumn = 0; pivotColumn < 3; pivotColumn++)
        {
            int pivotRow = pivotColumn;
            double largest = Math.Abs(augmented[pivotRow, pivotColumn]);

            for (int row = pivotColumn + 1; row < 3; row++)
            {
                double candidate = Math.Abs(augmented[row, pivotColumn]);

                if (candidate > largest)
                {
                    largest = candidate;
                    pivotRow = row;
                }
            }

            if (largest < 1e-10)
            {
                return false;
            }

            if (pivotRow != pivotColumn)
            {
                for (int column = pivotColumn; column < 4; column++)
                {
                    double temporary = augmented[pivotColumn, column];
                    augmented[pivotColumn, column] = augmented[pivotRow, column];
                    augmented[pivotRow, column] = temporary;
                }
            }

            double pivot = augmented[pivotColumn, pivotColumn];

            for (int column = pivotColumn; column < 4; column++)
            {
                augmented[pivotColumn, column] /= pivot;
            }

            for (int row = 0; row < 3; row++)
            {
                if (row == pivotColumn)
                {
                    continue;
                }

                double factor = augmented[row, pivotColumn];

                for (int column = pivotColumn; column < 4; column++)
                {
                    augmented[row, column] -=
                        factor * augmented[pivotColumn, column];
                }
            }
        }

        solution[0] = augmented[0, 3];
        solution[1] = augmented[1, 3];
        solution[2] = augmented[2, 3];
        return true;
    }

    // =====================================================================
    // In-Game status panel (no TextMeshPro dependency)
    // =====================================================================

    private void OnGUI()
    {
        GUIStyle boxStyle = new GUIStyle(GUI.skin.box)
        {
            alignment = TextAnchor.UpperLeft,
            padding = new RectOffset(18, 18, 14, 14)
        };

        GUIStyle titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 25,
            fontStyle = FontStyle.Bold
        };

        GUIStyle normalStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 19,
            wordWrap = true
        };

        GUILayout.BeginArea(
            new Rect(20, 20, 680, 560),
            GUIContent.none,
            boxStyle);

        GUILayout.Label("AR Point Measurement", titleStyle);
        GUILayout.Space(8);
        GUILayout.Label("Stage: " + GetStageText(), normalStyle);

        if (stage == MeasurementStage.CoarseRayCapture)
        {
            GUILayout.Label("Rays: " + rays.Count + " / 4", normalStyle);
            GUILayout.Label("Progress: " + CreateRayProgressText(), normalStyle);

            if (hasCoarsePoint)
            {
                GUILayout.Label("q0 (mm): " + FormatVectorMm(coarsePointQ0), normalStyle);

                GUILayout.Label("q0-v residual: " + GetCurrentCoarseResidualText(), normalStyle);
            }

            GUILayout.Space(8);
            GUILayout.Label("Space (release): Capture; after 4 rays, enter fine stage", normalStyle);
            GUILayout.Label("Enter (2+ rays): Send coarse residual and enter fine stage", normalStyle);
            GUILayout.Label("Backspace: Remove last ray", normalStyle);
            GUILayout.Label("Delete: Clear rays", normalStyle);
        }
        else
        {
            GUILayout.Label("q0 (mm): " + FormatVectorMm(coarsePointQ0), normalStyle);
            GUILayout.Label("Confirmed: " + CreateSphereProgressText(), normalStyle);

            if (stage == MeasurementStage.FineSphereEditing && activeSphereIndex >= 0)
            {
                GUILayout.Label(
                    "Active plane: " + (activeSphereIndex + 1) +
                    " / 4, offset: " +
                    (spheres[activeSphereIndex].offsetMetres * MetresToMillimetres)
                    .ToString("F1") + " mm",
                    normalStyle);
            }

            if (hasFinePoint)
            {
                GUILayout.Label("Fine q (mm): " + FormatVectorMm(finePointQ), normalStyle);

                GUILayout.Label("q-v residual: " + GetCurrentFineResidualText(), normalStyle);
            }

            GUILayout.Space(8);
            GUILayout.Label("Left/Right: plane offset -/+ 1.0 mm", normalStyle);
            GUILayout.Label("Down/Up: plane offset -/+ 0.1 mm", normalStyle);
            GUILayout.Label("Space: Confirm active plane", normalStyle);
            GUILayout.Label("1-4: Select plane; switching auto-confirms current plane", normalStyle);
            GUILayout.Label("Enter: Calculate only after all planes are confirmed", normalStyle);
            GUILayout.Label("Delete: Clear after final result is shown", normalStyle);
        }

        GUILayout.Space(10);
        GUILayout.Label("Status: " + statusMessage, normalStyle);
        GUILayout.Label("Network: " + networkMessage, normalStyle);
        GUILayout.EndArea();
    }

    private string GetStageText()
    {
        switch (stage)
        {
            case MeasurementStage.CoarseRayCapture:
                return "Coarse ray capture";
            case MeasurementStage.FineSphereEditing:
                return "Fine plane editing";
            case MeasurementStage.FineResultShown:
                return "Fine result shown";
            default:
                return "Unknown";
        }
    }

    private string CreateRayProgressText()
    {
        string text = "";

        for (int i = 0; i < MaximumRayCount; i++)
        {
            text += i < rays.Count ? "[X] " : "[ ] ";
        }

        return text;
    }

    private string CreateSphereProgressText()
    {
        string text = "";

        for (int i = 0; i < SphereCount; i++)
        {
            text += spheres[i] != null && spheres[i].confirmed ? "[X] " : "[ ] ";
        }

        return text;
    }

    private string GetCurrentCoarseResidualText()
    {
        if (virtualPointV == null)
        {
            return "V is not assigned";
        }

        return
            (Vector3.Distance(coarsePointQ0, virtualPointV.position) *
             MetresToMillimetres).ToString("F3") + " mm";
    }

    private string GetCurrentFineResidualText()
    {
        if (virtualPointV == null)
        {
            return "V is not assigned";
        }

        return
            (Vector3.Distance(finePointQ, virtualPointV.position) *
             MetresToMillimetres).ToString("F3") + " mm";
    }

    private static string FormatVectorMm(Vector3 pointMetres)
    {
        Vector3 mm = ToMillimetres(pointMetres);
        return "(" + mm.x.ToString("F2") + ", " +
                     mm.y.ToString("F2") + ", " +
                     mm.z.ToString("F2") + ")";
    }

    private sealed class FineSphereState
    {
        public int index;
        public Vector3 centerWorld;
        public Vector3 normalWorld;
        public float offsetMetres;
        public bool confirmed;
        public GameObject root;
        public MeshRenderer meshRenderer;
    }
}

// =========================================================================
// TCP payloads. All numeric coordinates and distances in these messages are mm.
// Each JSON message is compact and terminated by a single newline.
// =========================================================================

[Serializable]
public class CoarseResultMessage
{
    public string messageType;
    public string unit;
    public int rayCount;
    public Vector3 qMm;
    public Vector3 vMm;
    public float absoluteResidualMm;

    // Milliseconds from capturing Ray 1 to this coarse result being sent.
    public float measurementDurationMs;
}

[Serializable]
public class SphereConfirmationMessage
{
    public string messageType;
    public string unit;
    public int sphereIndex;
    public string confirmationSource;
    public Vector3 centerMm;
    public Vector3 normal;
    public Vector3 vMm;
    public float radiusMm;
    public float centerToVMm;
    public float signedSurfaceResidualMm;
    public float absoluteSurfaceResidualMm;
}

[Serializable]
public class FineResultMessage
{
    public string messageType;
    public string unit;
    public Vector3 q0Mm;
    public Vector3 qMm;
    public Vector3 vMm;
    public float absoluteResidualMm;
    public List<FineSphereReport> spheres;
    public List<FineSphereReport> planes;

    // Milliseconds from capturing Ray 1 to this fine result being computed.
    public float measurementDurationMs;
}

[Serializable]
public class FineSphereReport
{
    public int sphereIndex;
    public Vector3 centerMm;
    public Vector3 normal;
    public float radiusMm;
    public float centerToVMm;
    public float signedSurfaceResidualMm;
    public float absoluteSurfaceResidualMm;
}

[Serializable]
public class ARMeasurementRayData
{
    public Vector3 pointA;
    public Vector3 pointB;
}
