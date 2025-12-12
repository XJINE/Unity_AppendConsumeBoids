using UnityEngine;

namespace GpuBoids {
public partial class AppendConsumeBoids
{
    public static class ShaderPropertyIds
    {
        public static readonly int _BoidsAgentBufferRead     = Shader.PropertyToID(nameof(_BoidsAgentBufferRead));
        public static readonly int _BoidsAgentBufferWrite    = Shader.PropertyToID(nameof(_BoidsAgentBufferWrite));
        public static readonly int _BoidsForceBufferRead     = Shader.PropertyToID(nameof(_BoidsForceBufferRead));
        public static readonly int _BoidsForceBufferWrite    = Shader.PropertyToID(nameof(_BoidsForceBufferWrite));
        public static readonly int _EmitAgentBuffer          = Shader.PropertyToID(nameof(_EmitAgentBuffer));
        public static readonly int _PooledAgentBufferAppend  = Shader.PropertyToID(nameof(_PooledAgentBufferAppend));
        public static readonly int _PooledAgentBufferConsume = Shader.PropertyToID(nameof(_PooledAgentBufferConsume));
        public static readonly int _EmitAgentCount           = Shader.PropertyToID(nameof(_EmitAgentCount));
        public static readonly int _MaxAgentCount            = Shader.PropertyToID(nameof(_MaxAgentCount));
        public static readonly int _MaxAgentSpeed            = Shader.PropertyToID(nameof(_MaxAgentSpeed));
        public static readonly int _MaxAgentForce            = Shader.PropertyToID(nameof(_MaxAgentForce));
        public static readonly int _DeltaTime                = Shader.PropertyToID(nameof(_DeltaTime));
        public static readonly int _SeparateRadius           = Shader.PropertyToID(nameof(_SeparateRadius));
        public static readonly int _AlignmentRadius          = Shader.PropertyToID(nameof(_AlignmentRadius));
        public static readonly int _CohesionRadius           = Shader.PropertyToID(nameof(_CohesionRadius));
        public static readonly int _SeparateWeight           = Shader.PropertyToID(nameof(_SeparateWeight));
        public static readonly int _AlignmentWeight          = Shader.PropertyToID(nameof(_AlignmentWeight));
        public static readonly int _CohesionWeight           = Shader.PropertyToID(nameof(_CohesionWeight));
        public static readonly int _AvoidWallWeight          = Shader.PropertyToID(nameof(_AvoidWallWeight));
        public static readonly int _WallCenter               = Shader.PropertyToID(nameof(_WallCenter));
        public static readonly int _WallSize                 = Shader.PropertyToID(nameof(_WallSize));
    }
}}