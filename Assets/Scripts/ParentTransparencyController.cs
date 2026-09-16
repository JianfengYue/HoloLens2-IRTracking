using System.Collections.Generic;
using UnityEngine;

public class ParentTransparencyController : MonoBehaviour
{
    [Header("Parent Objects")]
    [Tooltip("Assign one or more parent GameObjects. All Renderers under them will be controlled.")]
    public GameObject[] parentObjects;

    [Header("Alpha Settings")]
    [Range(0f, 1f)]
    public float currentAlpha = 1f;

    [Range(0.01f, 1f)]
    public float alphaStep = 0.25f;

    [Header("Search Settings")]
    [Tooltip("Also include inactive child objects.")]
    public bool includeInactiveChildren = true;

    private readonly List<Material> controlledMaterials = new List<Material>();

    private void Start()
    {
        CollectMaterials();
        ApplyAlpha();
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.N))
        {
            ChangeAlpha(-alphaStep);
        }

        if (Input.GetKeyDown(KeyCode.M))
        {
            ChangeAlpha(alphaStep);
        }
    }

    private void CollectMaterials()
    {
        controlledMaterials.Clear();

        if (parentObjects == null || parentObjects.Length == 0)
        {
            Debug.LogError("No parent objects assigned.");
            enabled = false;
            return;
        }

        foreach (GameObject parentObject in parentObjects)
        {
            if (parentObject == null)
            {
                continue;
            }

            Renderer[] renderers =
                parentObject.GetComponentsInChildren<Renderer>(
                    includeInactiveChildren
                );

            foreach (Renderer targetRenderer in renderers)
            {
                Material[] rendererMaterials = targetRenderer.materials;

                foreach (Material material in rendererMaterials)
                {
                    if (material == null)
                    {
                        continue;
                    }

                    if (!controlledMaterials.Contains(material))
                    {
                        controlledMaterials.Add(material);
                    }
                }
            }
        }

        if (controlledMaterials.Count == 0)
        {
            Debug.LogWarning(
                "No materials were found under the assigned parent objects."
            );
        }
    }

    private void ChangeAlpha(float amount)
    {
        currentAlpha += amount;
        currentAlpha = Mathf.Clamp01(currentAlpha);

        // Keep the value exactly on 0%, 25%, 50%, 75%, or 100%.
        currentAlpha = Mathf.Round(currentAlpha / alphaStep) * alphaStep;
        currentAlpha = Mathf.Clamp01(currentAlpha);

        ApplyAlpha();

        Debug.Log(
            "Current Alpha: "
            + Mathf.RoundToInt(currentAlpha * 255f)
            + " / 255 ("
            + Mathf.RoundToInt(currentAlpha * 100f)
            + "%)"
        );
    }

    private void ApplyAlpha()
    {
        foreach (Material material in controlledMaterials)
        {
            if (material == null)
            {
                continue;
            }

            Color color = material.color;
            color.a = currentAlpha;
            material.color = color;
        }
    }
}