using UnityEngine;

public class TDU2UIManager : MonoBehaviour
{
    public static TDU2UIManager Instance { get; private set; }
    public TDU2Radio Radio { get; private set; }

    void Start()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this.gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(this.gameObject);
    }

    // Update is called once per frame
    void Update()
    {

    }
}
