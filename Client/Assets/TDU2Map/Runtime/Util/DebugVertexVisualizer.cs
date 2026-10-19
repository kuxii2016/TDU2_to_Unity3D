using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class DebugVertexVisualizer : MonoBehaviour
{
    [Header("Materials")]
    [SerializeField] private Material meshVertexMaterial;
    [SerializeField] private Material collisionVertexMaterial;

    [Header("Settings")]
    [SerializeField] private bool showOnStart = false;
    [SerializeField] private bool includeInactiveColliders = false;
    [SerializeField] private LayerMask collisionLayerMask = -1;

    [Header("Collision Filtering")]
    [SerializeField] private bool onlyMeshColliders = true;

    private bool _isVisible;
    private readonly List<Mesh> _collisionMeshes = new List<Mesh>(32);
    private readonly List<Matrix4x4> _collisionMatrices = new List<Matrix4x4>(32);

    private void Start()
    {
        _isVisible = showOnStart;
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F2))
        {
            _isVisible = !_isVisible;
        }

        if (!_isVisible)
            return;

        // Mesh Vertices (Rot)
        if (meshVertexMaterial != null)
        {
            var meshFilters = FindObjectsByType<MeshFilter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < meshFilters.Length; i++)
            {
                var mf = meshFilters[i];
                if (mf == null || mf.sharedMesh == null)
                    continue;

                var renderer = mf.GetComponent<MeshRenderer>();
                if (renderer != null && !renderer.enabled)
                    continue;

                // Skaliert + rotiert + positioniert korrekt
                Graphics.DrawMesh(mf.sharedMesh, mf.transform.localToWorldMatrix, meshVertexMaterial, mf.gameObject.layer, null, 0, null, false, false);
            }
        }

        // Collision Vertices (Grün)
        if (collisionVertexMaterial != null)
        {
            _collisionMeshes.Clear();
            _collisionMatrices.Clear();

            Collider[] colliders = includeInactiveColliders
                ? FindObjectsByType<Collider>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                : FindObjectsByType<Collider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            for (int i = 0; i < colliders.Length; i++)
            {
                var col = colliders[i];
                if (col == null || !col.enabled)
                    continue;

                if (((1 << col.gameObject.layer) & collisionLayerMask) == 0)
                    continue;

                if (onlyMeshColliders)
                {
                    var mc = col as MeshCollider;
                    if (mc == null || mc.sharedMesh == null || mc.convex)
                        continue; // Convex MeshCollider zeigt oft vereinfachte Geometrie, skip für saubere Debug-Anzeige

                    _collisionMeshes.Add(mc.sharedMesh);
                    _collisionMatrices.Add(mc.transform.localToWorldMatrix);
                }
                else
                {
                    // Optional: andere Collider (Box/Sphere etc.) haben keine Mesh-Verts
                    var mc = col as MeshCollider;
                    if (mc != null && mc.sharedMesh != null && !mc.convex)
                    {
                        _collisionMeshes.Add(mc.sharedMesh);
                        _collisionMatrices.Add(mc.transform.localToWorldMatrix);
                    }
                }
            }

            for (int i = 0; i < _collisionMeshes.Count; i++)
            {
                Graphics.DrawMesh(_collisionMeshes[i], _collisionMatrices[i], collisionVertexMaterial, 0, null, 0, null, false, false);
            }
        }
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        // Nur im Playmode relevant, Visualisierung läuft per DrawMesh
    }
#endif
}