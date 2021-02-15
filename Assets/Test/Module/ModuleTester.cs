using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using RenderHeads.NDI;
public class ModuleTester : MonoBehaviour
{
    [SerializeField]
    private string source = "DESKTOP-V86PAK4 (Test Pattern)";

    [SerializeField]
    private RenderTexture _texture;
    // Start is called before the first frame update

    private INDIModule ndiModule;

    public List<string> ExtraIPS = new List<string>();
    void Start()
    {
         ndiModule = new NDIModule(true,ExtraIPS);
          
    }

    [ContextMenu("start Stream")]
    public void StartStream()
    {
        _texture = ndiModule.CaptureStream(source, 1920,1080);
    }
    [ContextMenu("Mute")]

    public void Mute()
    {
        ndiModule.SetVolume(_texture, 0);
    }
    [ContextMenu("Full Volume")]


    public void EndStream()
    {
        ndiModule.ReleaseStream(_texture);
    }
    public void FullVolume()
    {
        ndiModule.SetVolume(_texture, 1);
    }
    // Update is called once per frame
    void Update()
    {
        ndiModule.UpdateModule();
    }
}
