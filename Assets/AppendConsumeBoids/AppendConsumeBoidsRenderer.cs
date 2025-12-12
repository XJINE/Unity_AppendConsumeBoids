using UnityEngine;
using PID = GpuBoids.AppendConsumeBoidsRenderer.ShaderPropertyIds;

namespace GpuBoids {
public partial class AppendConsumeBoidsRenderer
{
    public static class ShaderPropertyIds
    {
        public static readonly int _BoidsAgentBuffer = Shader.PropertyToID(nameof(_BoidsAgentBuffer));
        public static readonly int _AgentScale       = Shader.PropertyToID(nameof(_AgentScale));
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

     [ContextMenu(nameof(UpdateParameter))]
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

     [ContextMenu(nameof(UpdateBuffer))]
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
     }

     private void DisposeBuffer()
     {
         _commandBuffer?.Dispose();
         _commandBuffer = null;
     }
}}