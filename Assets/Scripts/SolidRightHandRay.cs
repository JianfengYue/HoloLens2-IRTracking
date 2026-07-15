using System.Collections.Generic;
using UnityEngine;
using Microsoft.MixedReality.Toolkit.Input;
using Microsoft.MixedReality.Toolkit.Utilities;

/// <summary>
/// Replaces the MRTK dashed right-hand ray with a solid line.
///
/// The ray can be blocked or reflected by colliders whose Renderer
/// uses the selected material.
/// </summary>
[DefaultExecutionOrder(10000)]
public sealed class SolidRightHandRay : MonoBehaviour
{
    public enum SelectedMaterialBehaviour
    {
        Block,
        Reflect
    }

    [Header("Collection Manager")]

    [Tooltip(
        "The existing RayCollectionManager. " +
        "It is found automatically when both scripts are on the same object.")]
    public RayCollectionManager collectionManager;

    [Header("Solid Ray Appearance")]

    [Min(0.001f)]
    [Tooltip("Thickness of the solid ray in metres.")]
    public float lineWidth = 0.008f;

    [Tooltip("Colour of the solid aiming ray.")]
    public Color lineColor = Color.cyan;

    [Min(0.1f)]
    [Tooltip("Used only when RayCollectionManager cannot be found.")]
    public float fallbackRayLength = 3.0f;

    [Tooltip("Hide the original MRTK dashed line.")]
    public bool hideOriginalMrtkRay = true;

    [Header("Material Interaction")]

    [Tooltip(
        "Objects using this material can block or reflect the ray. " +
        "Their GameObjects must also have Colliders.")]
    public Material selectedMaterial;

    [Tooltip("What the selected material does to the ray.")]
    public SelectedMaterialBehaviour selectedMaterialBehaviour =
        SelectedMaterialBehaviour.Block;

    [Tooltip(
        "Which physics layers are checked by the ray.")]
    public LayerMask raycastLayers = ~0;

    [Tooltip(
        "Whether trigger colliders can block or reflect the ray.")]
    public QueryTriggerInteraction triggerInteraction =
        QueryTriggerInteraction.Ignore;

    [Min(0.01f)]
    [Tooltip(
        "Maximum length of the reflected segment.")]
    public float reflectedRayLength = 2.0f;

    [Min(0f)]
    [Tooltip(
        "Small offset preventing the reflected ray from hitting " +
        "the same surface immediately.")]
    public float surfaceOffset = 0.002f;

    [Tooltip(
        "When enabled, non-selected materials also block the ray. " +
        "When disabled, they are ignored.")]
    public bool otherObjectsBlockRay = false;

    private LineRenderer solidLine;
    private Material runtimeMaterial;
    private LinePointer currentPointer;

    private readonly List<LineRenderer> hiddenMrtkLines =
        new List<LineRenderer>();

    private void Awake()
    {
        if (collectionManager == null)
        {
            collectionManager =
                GetComponent<RayCollectionManager>();
        }

        CreateSolidLine();
    }

    private void LateUpdate()
    {
        LinePointer rightHandPointer =
            FindActiveRightHandPointer();

        if (rightHandPointer == null)
        {
            solidLine.enabled = false;
            return;
        }

        if (rightHandPointer != currentPointer)
        {
            RestoreOriginalMrtkLines();
            currentPointer = rightHandPointer;
        }

        if (hideOriginalMrtkRay)
        {
            HideOriginalMrtkLines(rightHandPointer);
        }
        else
        {
            RestoreOriginalMrtkLines();
        }

        if (rightHandPointer.Rays == null ||
            rightHandPointer.Rays.Length == 0)
        {
            solidLine.enabled = false;
            return;
        }

        Vector3 origin =
            rightHandPointer.Rays[0].Origin;

        Vector3 direction =
            rightHandPointer.Rays[0].Direction;

        if (direction.sqrMagnitude < 0.000001f)
        {
            solidLine.enabled = false;
            return;
        }

        direction.Normalize();

        float length =
            GetDisplayLength();

        DrawInteractiveRay(
            origin,
            direction,
            length);
    }

    private void DrawInteractiveRay(
        Vector3 origin,
        Vector3 direction,
        float maximumLength)
    {
        solidLine.enabled = true;

        if (!TryFindRelevantHit(
                origin,
                direction,
                maximumLength,
                out RaycastHit hit,
                out bool usesSelectedMaterial))
        {
            DrawStraightRay(
                origin,
                origin + direction * maximumLength);

            return;
        }

        if (!usesSelectedMaterial)
        {
            // A normal object was hit, and otherObjectsBlockRay is enabled.
            DrawStraightRay(
                origin,
                hit.point);

            return;
        }

        if (selectedMaterialBehaviour ==
            SelectedMaterialBehaviour.Block)
        {
            DrawStraightRay(
                origin,
                hit.point);

            return;
        }

        DrawReflectedRay(
            origin,
            direction,
            hit);
    }

    private void DrawStraightRay(
        Vector3 origin,
        Vector3 endPoint)
    {
        solidLine.positionCount = 2;

        solidLine.SetPosition(
            0,
            origin);

        solidLine.SetPosition(
            1,
            endPoint);
    }

    private void DrawReflectedRay(
        Vector3 origin,
        Vector3 incomingDirection,
        RaycastHit firstHit)
    {
        Vector3 reflectedDirection =
            Vector3.Reflect(
                incomingDirection,
                firstHit.normal).normalized;

        Vector3 reflectedOrigin =
            firstHit.point +
            firstHit.normal * surfaceOffset;

        Vector3 reflectedEnd =
            reflectedOrigin +
            reflectedDirection * reflectedRayLength;

        // Optionally stop the reflected ray at another collider.
        if (TryFindRelevantHit(
                reflectedOrigin,
                reflectedDirection,
                reflectedRayLength,
                out RaycastHit reflectedHit,
                out _))
        {
            reflectedEnd =
                reflectedHit.point;
        }

        solidLine.positionCount = 3;

        solidLine.SetPosition(
            0,
            origin);

        solidLine.SetPosition(
            1,
            firstHit.point);

        solidLine.SetPosition(
            2,
            reflectedEnd);
    }

    private bool TryFindRelevantHit(
        Vector3 origin,
        Vector3 direction,
        float maximumLength,
        out RaycastHit relevantHit,
        out bool usesSelectedMaterial)
    {
        relevantHit = default;
        usesSelectedMaterial = false;

        RaycastHit[] hits =
            Physics.RaycastAll(
                origin,
                direction,
                maximumLength,
                raycastLayers,
                triggerInteraction);

        if (hits == null || hits.Length == 0)
        {
            return false;
        }

        System.Array.Sort(
            hits,
            (a, b) =>
                a.distance.CompareTo(b.distance));

        foreach (RaycastHit hit in hits)
        {
            if (hit.collider == null)
            {
                continue;
            }

            bool materialMatches =
                ColliderUsesSelectedMaterial(
                    hit.collider);

            if (materialMatches)
            {
                relevantHit = hit;
                usesSelectedMaterial = true;
                return true;
            }

            if (otherObjectsBlockRay)
            {
                relevantHit = hit;
                usesSelectedMaterial = false;
                return true;
            }
        }

        return false;
    }

    private bool ColliderUsesSelectedMaterial(
        Collider targetCollider)
    {
        if (selectedMaterial == null ||
            targetCollider == null)
        {
            return false;
        }

        Renderer targetRenderer =
            targetCollider.GetComponent<Renderer>();

        if (targetRenderer == null)
        {
            targetRenderer =
                targetCollider.GetComponentInParent<Renderer>();
        }

        if (targetRenderer == null)
        {
            targetRenderer =
                targetCollider.GetComponentInChildren<Renderer>();
        }

        if (targetRenderer == null)
        {
            return false;
        }

        Material[] materials =
            targetRenderer.sharedMaterials;

        foreach (Material material in materials)
        {
            if (material == null)
            {
                continue;
            }

            // 完全是同一个材质资源。
            if (material == selectedMaterial)
            {
                return true;
            }

            // 兼容运行时生成的 "Bone (Instance)" 材质。
            string detectedName =
                material.name.Replace(" (Instance)", "");

            string selectedName =
                selectedMaterial.name.Replace(" (Instance)", "");

            if (detectedName == selectedName)
            {
                return true;
            }
        }

        return false;
    }

    private LinePointer FindActiveRightHandPointer()
    {
        foreach (
            LinePointer pointer in
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

            return pointer;
        }

        return null;
    }

    private float GetDisplayLength()
    {
        if (collectionManager != null)
        {
            return collectionManager.rayLength;
        }

        return fallbackRayLength;
    }

    private void CreateSolidLine()
    {
        GameObject lineObject =
            new GameObject("LiveSolidRightHandRay");

        lineObject.transform.SetParent(
            transform,
            false);

        solidLine =
            lineObject.AddComponent<LineRenderer>();

        solidLine.useWorldSpace = true;
        solidLine.positionCount = 2;

        solidLine.startWidth = lineWidth;
        solidLine.endWidth = lineWidth;

        solidLine.startColor = lineColor;
        solidLine.endColor = lineColor;

        solidLine.numCapVertices = 8;
        solidLine.numCornerVertices = 4;

        solidLine.alignment =
            LineAlignment.View;

        solidLine.textureMode =
            LineTextureMode.Stretch;

        Shader shader =
            Shader.Find("Sprites/Default");

        if (shader == null)
        {
            shader =
                Shader.Find("Unlit/Color");
        }

        if (shader == null)
        {
            Debug.LogError(
                "No suitable shader was found for " +
                "the solid right-hand ray.");

            solidLine.enabled = false;
            return;
        }

        runtimeMaterial =
            new Material(shader);

        runtimeMaterial.name =
            "Runtime Solid Right Hand Ray Material";

        solidLine.sharedMaterial =
            runtimeMaterial;

        solidLine.enabled = false;
    }

    private void HideOriginalMrtkLines(
        LinePointer pointer)
    {
        LineRenderer[] mrtkLines =
            pointer.GetComponentsInChildren<LineRenderer>(
                true);

        foreach (LineRenderer mrtkLine in mrtkLines)
        {
            if (mrtkLine == null ||
                mrtkLine == solidLine)
            {
                continue;
            }

            if (!hiddenMrtkLines.Contains(mrtkLine))
            {
                hiddenMrtkLines.Add(mrtkLine);
            }

            mrtkLine.enabled = false;
        }
    }

    private void RestoreOriginalMrtkLines()
    {
        foreach (LineRenderer mrtkLine in hiddenMrtkLines)
        {
            if (mrtkLine != null)
            {
                mrtkLine.enabled = true;
            }
        }

        hiddenMrtkLines.Clear();
    }

    private void OnValidate()
    {
        if (solidLine == null)
        {
            return;
        }

        solidLine.startWidth = lineWidth;
        solidLine.endWidth = lineWidth;

        solidLine.startColor = lineColor;
        solidLine.endColor = lineColor;
    }

    private void OnDisable()
    {
        if (solidLine != null)
        {
            solidLine.enabled = false;
        }

        RestoreOriginalMrtkLines();
    }

    private void OnDestroy()
    {
        RestoreOriginalMrtkLines();

        if (runtimeMaterial != null)
        {
            Destroy(runtimeMaterial);
        }
    }
}