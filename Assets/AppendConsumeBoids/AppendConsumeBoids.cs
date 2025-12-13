using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using PID        = GpuBoids.AppendConsumeBoids.ShaderPropertyIds;
using BufferType = UnityEngine.GraphicsBuffer.Target;

namespace GpuBoids {
public struct BoidsAgent
{
    public Vector3 position;
    public Vector3 velocity;
    public float   lifeTime;
    public int     status;   // 0: unused, 1: live
}

[Serializable]
public class BoidsParameter
{
    public int maxAgentCount  = 100000;
    public int emitAgentCount = 10;

    public float maxAgentSpeed = 5.0f;
    public float maxAgentForce = 0.5f;

    public float cohesionRadius  = 2.0f;
    public float alignmentRadius = 2.0f;
    public float separateRadius  = 1.0f;

    public float cohesionWeight  = 1.0f;
    public float alignmentWeight = 1.0f;
    public float separateWeight  = 3.0f;
    public float avoidWallWeight = 10.0f;
}

[Serializable]
public class FieldParameter
{
    public Vector3 wallCenter = Vector3.zero;
    public Vector3 wallSize   = new (32.0f, 32.0f, 32.0f);
}

public partial class AppendConsumeBoids : MonoBehaviour
{
    #region Field

    [SerializeField] private BoidsParameter boidsParameter;
    [SerializeField] private FieldParameter fieldParameter;
    [SerializeField] private ComputeShader  boidsComputeShader;

    public UnityEvent parameterUpdated;
    public UnityEvent bufferUpdated;

    private int _kernelIndexInitializePool;
    private int _kernelIndexUpdateStatus;
    private int _kernelIndexEmitAgent;
    private int _kernelIndexUpdateForce;
    private int _kernelIndexUpdateAgent;

    private Vector3Int _threadGroupSizeInitializePool;
    private Vector3Int _threadGroupSizeUpdateStatus;
    private Vector3Int _threadGroupSizeEmitAgent;
    private Vector3Int _threadGroupSizeUpdateForce;
    private Vector3Int _threadGroupSizeUpdateAgent;

    private Vector3   []     _boidsForceBuffer;
    private BoidsAgent[]     _boidsAgentBuffer;
    private uint      []     _pooledAgentBuffer;
    private uint      []     _pooledCountBuffer;
    private List<BoidsAgent> _emitAgentsBuffer;

    #endregion Field

    #region Property

    public BoidsParameter BoidsParameter => boidsParameter;
    public FieldParameter FieldParameter => fieldParameter;

    public GraphicsBuffer BoidsForceBuffer  { get; private set; }
    public GraphicsBuffer BoidsAgentBuffer  { get; private set; }
    public GraphicsBuffer PooledAgentBuffer { get; private set; }
    public GraphicsBuffer PooledCountBuffer { get; private set; }
    public GraphicsBuffer EmitAgentBuffer   { get; private set; }

    public int PooledAgentCount { get; private set; }

    #endregion Property

    private void Awake()
    {
        var cs = boidsComputeShader;

        _kernelIndexInitializePool = cs.FindKernel("InitializePool");
        _kernelIndexUpdateStatus   = cs.FindKernel("UpdateStatus");
        _kernelIndexEmitAgent      = cs.FindKernel("EmitAgent");
        _kernelIndexUpdateForce    = cs.FindKernel("UpdateForce");
        _kernelIndexUpdateAgent    = cs.FindKernel("UpdateAgent");

        _threadGroupSizeInitializePool = GetThreadGroupSize(_kernelIndexInitializePool);
        _threadGroupSizeUpdateStatus   = GetThreadGroupSize(_kernelIndexUpdateStatus);
        _threadGroupSizeEmitAgent      = GetThreadGroupSize(_kernelIndexEmitAgent);
        _threadGroupSizeUpdateForce    = GetThreadGroupSize(_kernelIndexUpdateForce);
        _threadGroupSizeUpdateAgent    = GetThreadGroupSize(_kernelIndexUpdateAgent);

        return;

        Vector3Int GetThreadGroupSize(int kernelIndex)
        {
            cs.GetKernelThreadGroupSizes(kernelIndex, out var x, out var y, out var z);
            return new Vector3Int((int) x, (int) y, (int) z);
        }
    }

    private void Start()
    {
        UpdateBuffer();
        UpdateParameter();
    }

    private void Update()
    {
        // Update Pool data.
        GraphicsBuffer.CopyCount(PooledAgentBuffer, PooledCountBuffer, 0);
        PooledCountBuffer.GetData(_pooledCountBuffer);
        PooledAgentCount = (int)_pooledCountBuffer[0];

        var mouse = Mouse.current;

        if (mouse != null && mouse.leftButton.isPressed
        && boidsParameter.emitAgentCount < PooledAgentCount)
        {
            for (var i = 0; i < boidsParameter.emitAgentCount; i++)
            {
                var mousePos = (Vector3) mouse.position.ReadValue();
                var position = Camera.main.ScreenToWorldPoint(mousePos + Vector3.forward * 10);

                _emitAgentsBuffer.Add(new BoidsAgent()
                {
                    position = position,
                    velocity = UnityEngine.Random.onUnitSphere * boidsParameter.maxAgentSpeed,
                    lifeTime = 60,
                    status   = 1
                });
            }
        }

        Simulate();

        _emitAgentsBuffer.Clear();
    }

    private void OnDestroy()
    {
        DisposeBuffer();
    }

    [ContextMenu(nameof(UpdateParameter))]
    public void UpdateParameter()
    {
        var cs    = boidsComputeShader;
        var param = boidsParameter;
        var field = fieldParameter;

        cs.SetFloat (PID._MaxAgentSpeed,   param.maxAgentSpeed);
        cs.SetFloat (PID._MaxAgentForce,   param.maxAgentForce);
        cs.SetFloat (PID._CohesionRadius,  param.cohesionRadius);
        cs.SetFloat (PID._AlignmentRadius, param.alignmentRadius);
        cs.SetFloat (PID._SeparateRadius,  param.separateRadius);
        cs.SetFloat (PID._SeparateWeight,  param.separateWeight);
        cs.SetFloat (PID._CohesionWeight,  param.cohesionWeight);
        cs.SetFloat (PID._AlignmentWeight, param.alignmentWeight);
        cs.SetFloat (PID._AvoidWallWeight, param.avoidWallWeight);

        cs.SetVector(PID._WallCenter, field.wallCenter);
        cs.SetVector(PID._WallSize,   field.wallSize);

        parameterUpdated.Invoke();
    }

    [ContextMenu(nameof(UpdateBuffer))]
    public void UpdateBuffer()
    {
        var cs    = boidsComputeShader;
        var param = boidsParameter;

        cs.SetInt(PID._MaxAgentCount, param.maxAgentCount);

        InitializeBuffer();

        bufferUpdated.Invoke();
    }

    private void InitializeBuffer()
    {
        DisposeBuffer();

        // NOTE:
        // TThe maxCount should be updated whenever the Buffer is updated.
        var cs       = boidsComputeShader;
        var maxCount = boidsParameter.maxAgentCount;
        cs.SetInt(PID._MaxAgentCount, maxCount);

        _boidsForceBuffer = new Vector3[maxCount];
        BoidsForceBuffer  = new GraphicsBuffer(BufferType.Structured, maxCount, Marshal.SizeOf(typeof(Vector3)));
        BoidsForceBuffer.SetData(_boidsForceBuffer);

        _boidsAgentBuffer = new BoidsAgent[maxCount];
        BoidsAgentBuffer  = new GraphicsBuffer(BufferType.Structured, maxCount, Marshal.SizeOf(typeof(BoidsAgent)));
        BoidsAgentBuffer.SetData(_boidsAgentBuffer);

        _pooledAgentBuffer = new uint[maxCount];
        PooledAgentBuffer  = new GraphicsBuffer(BufferType.Append, maxCount, Marshal.SizeOf(typeof(uint)));
        PooledAgentBuffer.SetCounterValue(0);

        _pooledCountBuffer = new uint[] { 0 };
        PooledCountBuffer  = new GraphicsBuffer(BufferType.IndirectArguments, 1, Marshal.SizeOf(typeof(uint)));
        PooledCountBuffer.SetData(_pooledCountBuffer);

        _emitAgentsBuffer = new List<BoidsAgent>(maxCount);
        EmitAgentBuffer   = new GraphicsBuffer(BufferType.Structured, maxCount, Marshal.SizeOf(typeof(BoidsAgent)));
        EmitAgentBuffer.SetData(_emitAgentsBuffer);

        // SetBuffer

        cs.SetBuffer(_kernelIndexUpdateStatus, PID._BoidsAgentBufferWrite,   BoidsAgentBuffer);
        cs.SetBuffer(_kernelIndexUpdateStatus, PID._PooledAgentBufferAppend, PooledAgentBuffer);

        cs.SetBuffer(_kernelIndexEmitAgent, PID._PooledAgentBufferConsume, PooledAgentBuffer);
        cs.SetBuffer(_kernelIndexEmitAgent, PID._BoidsAgentBufferWrite,    BoidsAgentBuffer);

        cs.SetBuffer(_kernelIndexUpdateForce, PID._BoidsAgentBufferRead,  BoidsAgentBuffer);
        cs.SetBuffer(_kernelIndexUpdateForce, PID._BoidsForceBufferWrite, BoidsForceBuffer);

        cs.SetBuffer(_kernelIndexUpdateAgent, PID._BoidsAgentBufferWrite, BoidsAgentBuffer);
        cs.SetBuffer(_kernelIndexUpdateAgent, PID._BoidsForceBufferRead,  BoidsForceBuffer);

        // NOTE:
        // Append/Consume buffer must be initialized by ComputeShader.
        boidsComputeShader.SetInt   (PID._MaxAgentCount, maxCount);
        boidsComputeShader.SetBuffer(_kernelIndexInitializePool, PID._PooledAgentBufferAppend, PooledAgentBuffer);
        boidsComputeShader.Dispatch (_kernelIndexInitializePool, Mathf.CeilToInt((float)maxCount / _threadGroupSizeInitializePool.x), 1, 1);
    }

    private void DisposeBuffer()
    {
        BoidsForceBuffer?.Dispose();
        BoidsForceBuffer = null;

        BoidsAgentBuffer?.Dispose();
        BoidsAgentBuffer = null;

        PooledAgentBuffer?.Dispose();
        PooledAgentBuffer = null;

        PooledCountBuffer?.Dispose();
        PooledCountBuffer = null;

        EmitAgentBuffer?.Dispose();
        EmitAgentBuffer = null;
    }

    private void Simulate()
    {
        var threadGroups = Vector3Int.one;
        var cs           = boidsComputeShader;

        cs.SetFloat(PID._DeltaTime, Time.deltaTime);

        threadGroups = GetThreadGroups(boidsParameter.maxAgentCount, _threadGroupSizeUpdateStatus);
        cs.Dispatch(_kernelIndexUpdateStatus, threadGroups.x, threadGroups.y, threadGroups.z);

        var emitAgentCount = _emitAgentsBuffer.Count;
        threadGroups = GetThreadGroups(emitAgentCount, _threadGroupSizeEmitAgent);
        if (0 < threadGroups.x)
        {
            cs.SetInt(PID._EmitAgentCount, emitAgentCount);
            EmitAgentBuffer.SetData(_emitAgentsBuffer);
            cs.SetBuffer(_kernelIndexEmitAgent, PID._EmitAgentBuffer, EmitAgentBuffer);
            cs.Dispatch (_kernelIndexEmitAgent, threadGroups.x, threadGroups.y, threadGroups.z);
        }

        threadGroups = GetThreadGroups(boidsParameter.maxAgentCount, _threadGroupSizeUpdateForce);
        cs.Dispatch(_kernelIndexUpdateForce, threadGroups.x, threadGroups.y, threadGroups.z);

        threadGroups = GetThreadGroups(boidsParameter.maxAgentCount, _threadGroupSizeUpdateAgent);
        cs.Dispatch(_kernelIndexUpdateAgent, threadGroups.x, threadGroups.y, threadGroups.z);

        return;

        Vector3Int GetThreadGroups(int dataCount, Vector3Int threadGroupSize)
        {
            return new Vector3Int(Mathf.CeilToInt((float)dataCount / threadGroupSize.x), 1, 1);
        }
    }
}}