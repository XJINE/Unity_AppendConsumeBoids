using UnityEngine;
using PID = GpuBoids.AppendConsumeBoidsRenderer.ShaderPropertyIds;

namespace GpuBoids {
public partial class AppendConsumeBoidsRenderer
{
    public static class ShaderPropertyIds
    {
        public static readonly int _BoidsAgentBuffer = Shader.PropertyToID(nameof(_BoidsAgentBuffer));
        public static readonly int _AgentScale       = Shader.PropertyToID(nameof(_AgentScale));
        public static readonly int _GridColors       = Shader.PropertyToID(nameof(_GridColors));
    }
}

[RequireComponent(typeof(AppendConsumeBoids))]
public partial class AppendConsumeBoidsRenderer:MonoBehaviour
{
     #region Field

     public Vector3  agentScale = new (0.1f, 0.2f, 0.5f);
     public Mesh     agentMesh;
     public Material agentMaterial;

     private AppendConsumeBoids _appendConsumeBoids;
     private GraphicsBuffer     _commandBuffer;
     private RenderParams       _renderParams;
     private Color[]            _gridColors;
     private GraphicsBuffer     _gridColorBuffer;

     #endregion Field

     private void Awake()
     {
         _appendConsumeBoids = GetComponent<AppendConsumeBoids>();
         _appendConsumeBoids.parameterUpdated.AddListener(UpdateParameter);
         _appendConsumeBoids.bufferUpdated.AddListener(UpdateBuffer);
     }

     private void Start()
     {
         UpdateBuffer();
         UpdateParameter();
     }

     private void Update()
     {
         Graphics.RenderMeshIndirect(_renderParams, agentMesh, _commandBuffer);
     }

     private void OnDisable()
     {
        DisposeBuffer();
     }

     private void OnGUI()
     {
         var boids = _appendConsumeBoids;
         GUILayout.Label($"{nameof(boids.PooledAgentCount)} : {boids.PooledAgentCount}");
     }

     private void OnDrawGizmos()
     {
         if (Application.isPlaying)
         {
             var field = _appendConsumeBoids.FieldParameter;
             Gizmos.DrawWireCube(field.wallCenter, field.wallSize);
         }
     }

     public void UpdateParameter()
     {
         var field  = _appendConsumeBoids.FieldParameter;
         var bounds = new Bounds(field.wallCenter, field.wallSize);

         _renderParams = new RenderParams(agentMaterial)
         {
            worldBounds = bounds
         };

         agentMaterial.SetVector(PID._AgentScale, agentScale);
     }

     public void UpdateBuffer()
     {
         agentMaterial.SetBuffer(PID._BoidsAgentBuffer, _appendConsumeBoids.BoidsAgentBuffer);
         InitializeBuffer();
     }

     private void InitializeBuffer()
     {
         DisposeBuffer();

         var param = _appendConsumeBoids.BoidsParameter;
         var args  = new uint[]
         {
             agentMesh.GetIndexCount(0), // index count of mesh
             (uint)param.maxAgentCount,  // instance count
             0,                          // start index location of mesh
             0,                          // base vertex location of mesh
             0,                          // start instance location of instances
         };

         _commandBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, args.Length, sizeof(uint));
         _commandBuffer.SetData(args);

         
         var gridDivision = _appendConsumeBoids.FieldParameter.gridDivision;

         _gridColors = new Color[gridDivision.x * gridDivision.y * gridDivision.z];

         for(var i = 0; i < _gridColors.Length; i++)
         {
             Random.InitState(12345 + i);
             _gridColors[i] = Random.ColorHSV(0, 1, 1, 1, 1, 1, 1, 1);
         }

         _gridColorBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _gridColors.Length, sizeof(float) * 4);
         _gridColorBuffer.SetData(_gridColors);

         agentMaterial.SetBuffer(PID._GridColors, _gridColorBuffer);
     }

     private void DisposeBuffer()
     {
         _commandBuffer?.Dispose();
         _commandBuffer = null;

         _gridColorBuffer?.Dispose();
         _gridColorBuffer = null;
     }
}}