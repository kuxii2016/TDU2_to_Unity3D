using UnityEngine;

[ExecuteAlways]
[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class VolumetricCloudsRenderer : MonoBehaviour
{
    [Header("Bounds")]
    public Vector3 size = new Vector3(30000f, 550f, 30000f);
    public Vector3 offset = Vector3.zero;

    private MeshRenderer mr;
    private Material cloudMat;

    void OnEnable()
    {
        mr = GetComponent<MeshRenderer>();
        var mf = GetComponent<MeshFilter>();
        if (mf.sharedMesh == null)
            mf.sharedMesh = CreateBox();
        cloudMat = mr.sharedMaterial;
    }

    void Update()
    {
        if (cloudMat == null) return;
        Transform t = transform;
        Vector3 worldCenter = t.position + offset;
        Vector3 boundsMin = worldCenter - size * 0.5f;
        Vector3 boundsMax = worldCenter + size * 0.5f;
        cloudMat.SetVector("_CloudBoundsMin", boundsMin);
        cloudMat.SetVector("_CloudBoundsMax", boundsMax);
    }

    void OnDrawGizmos()
    {
        Gizmos.color = Color.cyan * 0.25f;
        Gizmos.DrawWireCube(transform.position + offset, size);
    }

    Mesh CreateBox()
    {
        Mesh m = new Mesh { name = "CloudVolumeBox" };
        Vector3[] v = new Vector3[8];
        v[0] = new Vector3(-0.5f, -0.5f, -0.5f); v[1] = new Vector3(0.5f, -0.5f, -0.5f); v[2] = new Vector3(0.5f, 0.5f, -0.5f); v[3] = new Vector3(-0.5f, 0.5f, -0.5f);
        v[4] = new Vector3(-0.5f, -0.5f, 0.5f); v[5] = new Vector3(0.5f, -0.5f, 0.5f); v[6] = new Vector3(0.5f, 0.5f, 0.5f); v[7] = new Vector3(-0.5f, 0.5f, 0.5f);
        int[] tris = new int[] { 0, 2, 1, 0, 3, 2, 1, 2, 6, 1, 6, 5, 5, 6, 7, 5, 7, 4, 4, 7, 3, 4, 3, 0, 3, 6, 2, 3, 7, 6, 4, 1, 5, 4, 0, 1 };
        m.vertices = v; m.triangles = tris; m.RecalculateBounds(); m.RecalculateNormals();
        return m;
    }
}