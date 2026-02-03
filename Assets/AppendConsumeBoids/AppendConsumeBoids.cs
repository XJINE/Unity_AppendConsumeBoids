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
    public uint    gridIndex;
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
    public Vector3    wallCenter   = Vector3.zero;
    public Vector3    wallSize     = new (32.0f, 32.0f, 32.0f);
    public Vector3Int gridDivision = new (8, 8, 8);
}

public partial class AppendConsumeBoids : MonoBehaviour
{
    #region Field

    [SerializeField] private BoidsParameter boidsParameter;
    [SerializeField] private FieldParameter fieldParameter;
    [SerializeField] private ComputeShader  boidsComputeShader;

    public UnityEvent parameterUpdated;
    public UnityEvent bufferUpdated;

    private int _kernelInitializePool;
    private int _kernelUpdateStatus;
    private int _kernelEmitAgent;
    private int _kernelUpdateForce;
    private int _kernelUpdateAgent;
    private int _kernelBitonicSort;
    private int _kernelUpdateGridIndices;

    private Vector3Int _tgsInitializePool;
    private Vector3Int _tgsUpdateStatus;
    private Vector3Int _tgsEmitAgent;
    private Vector3Int _tgsUpdateForce;
    private Vector3Int _tgsUpdateAgent;
    private Vector3Int _tgsBitonicSort;
    private Vector3Int _tgsUpdateGridIndices;

    private uint[]           _pooledCountBuffer;
    private uint[]           _agentIndexBuffer;
    private List<BoidsAgent> _emitAgentsBuffer;

    #endregion Field

    #region Property

    public BoidsParameter BoidsParameter => boidsParameter;
    public FieldParameter FieldParameter => fieldParameter;

    public GraphicsBuffer BoidsForceBuffer  { get; private set; }
    public GraphicsBuffer BoidsAgentBuffer  { get; private set; }
    public GraphicsBuffer PooledAgentBuffer { get; private set; }
    public GraphicsBuffer PooledCountBuffer { get; private set; }
    public GraphicsBuffer AgentIndexBuffer  { get; private set; }
    public GraphicsBuffer EmitAgentBuffer   { get; private set; }
    public GraphicsBuffer GridIndicesBuffer { get; private set; }

    public int PooledAgentCount { get; private set; }

    #endregion Property

    private void Awake()
    {
        var cs = boidsComputeShader;

        _kernelInitializePool    = cs.FindKernel("InitializePool");
        _kernelUpdateStatus      = cs.FindKernel("UpdateStatus");
        _kernelEmitAgent         = cs.FindKernel("EmitAgent");
        _kernelUpdateForce       = cs.FindKernel("UpdateForce");
        _kernelUpdateAgent       = cs.FindKernel("UpdateAgent");
        _kernelBitonicSort       = cs.FindKernel("BitonicSort");
        _kernelUpdateGridIndices = cs.FindKernel("UpdateGridIndices");

        _tgsInitializePool    = GetThreadGroupSize(_kernelInitializePool);
        _tgsUpdateStatus      = GetThreadGroupSize(_kernelUpdateStatus);
        _tgsEmitAgent         = GetThreadGroupSize(_kernelEmitAgent);
        _tgsUpdateForce       = GetThreadGroupSize(_kernelUpdateForce);
        _tgsUpdateAgent       = GetThreadGroupSize(_kernelUpdateAgent);
        _tgsBitonicSort       = GetThreadGroupSize(_kernelBitonicSort);
        _tgsUpdateGridIndices = GetThreadGroupSize(_kernelUpdateGridIndices);

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
        // Update Pool data before Simulate() for mouse click check
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
                    velocity = UnityEngine.Random.onUnitSphere,
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

        var gridMin      = field.wallCenter - field.wallSize * 0.5f;
        var gridCellSize = new Vector3(field.wallSize.x / field.gridDivision.x,
                                       field.wallSize.y / field.gridDivision.y,
                                       field.wallSize.z / field.gridDivision.z);
        var gridCellCount = field.gridDivision.x * field.gridDivision.y * field.gridDivision.z;

        cs.SetVector(PID._GridMinCoord,  gridMin);
        cs.SetVector(PID._GridCellSize,  gridCellSize);
        cs.SetInts  (PID._GridDivision,  field.gridDivision.x, field.gridDivision.y, field.gridDivision.z);
        cs.SetInt   (PID._GridCellCount, gridCellCount);

        parameterUpdated.Invoke();
    }

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
        // The maxCount should be updated whenever the Buffer is updated.
        var maxAgentCount = boidsParameter.maxAgentCount;
        
        _agentIndexBuffer = new uint[maxAgentCount];
        for (uint i = 0; i < maxAgentCount; i++)
        {
            _agentIndexBuffer[i] = i;
        }

        var gridDivision  = fieldParameter.gridDivision;
        var gridCellCount = gridDivision.x * gridDivision.y * gridDivision.z;
        var gridIndices   = new uint[gridCellCount * 2]; // * 2 for uint2.x, y

        for (var i = 0; i < gridIndices.Length; i++)
        {
            gridIndices[i] = uint.MaxValue; // Initialize with invalid indices
        }

        BoidsForceBuffer  = new GraphicsBuffer(BufferType.Structured, maxAgentCount, Marshal.SizeOf(typeof(Vector3)));
        BoidsForceBuffer.SetData(new Vector3[maxAgentCount]);

        BoidsAgentBuffer  = new GraphicsBuffer(BufferType.Structured, maxAgentCount, Marshal.SizeOf(typeof(BoidsAgent)));
        BoidsAgentBuffer.SetData(new BoidsAgent[maxAgentCount]);

        PooledAgentBuffer  = new GraphicsBuffer(BufferType.Append, maxAgentCount, Marshal.SizeOf(typeof(uint)));
        PooledAgentBuffer.SetCounterValue(0);

        _pooledCountBuffer = new uint[] { 0 };
        PooledCountBuffer  = new GraphicsBuffer(BufferType.IndirectArguments, 1, Marshal.SizeOf(typeof(uint)));
        PooledCountBuffer.SetData(_pooledCountBuffer);

        AgentIndexBuffer = new GraphicsBuffer(BufferType.Structured, maxAgentCount, Marshal.SizeOf(typeof(uint)));
        AgentIndexBuffer.SetData(_agentIndexBuffer);

        _emitAgentsBuffer = new List<BoidsAgent>(maxAgentCount);
        EmitAgentBuffer   = new GraphicsBuffer(BufferType.Structured, maxAgentCount, Marshal.SizeOf(typeof(BoidsAgent)));
        EmitAgentBuffer.SetData(_emitAgentsBuffer);

        GridIndicesBuffer = new GraphicsBuffer(BufferType.Structured, gridCellCount, Marshal.SizeOf(typeof(uint)) * 2);
        GridIndicesBuffer.SetData(gridIndices);

        var cs = boidsComputeShader;

        cs.SetInt(PID._MaxAgentCount, maxAgentCount);

        cs.SetBuffer(_kernelUpdateStatus, PID._BoidsAgentBufferWrite,   BoidsAgentBuffer);
        cs.SetBuffer(_kernelUpdateStatus, PID._PooledAgentBufferAppend, PooledAgentBuffer);

        cs.SetBuffer(_kernelEmitAgent, PID._PooledAgentBufferConsume, PooledAgentBuffer);
        cs.SetBuffer(_kernelEmitAgent, PID._BoidsAgentBufferWrite,    BoidsAgentBuffer);

        cs.SetBuffer(_kernelUpdateForce, PID._BoidsAgentBufferRead,  BoidsAgentBuffer);
        cs.SetBuffer(_kernelUpdateForce, PID._BoidsForceBufferWrite, BoidsForceBuffer);
        cs.SetBuffer(_kernelUpdateForce, PID._GridIndicesBuffer,     GridIndicesBuffer);
        cs.SetBuffer(_kernelUpdateForce, PID._AgentIndexBuffer,      AgentIndexBuffer);

        cs.SetBuffer(_kernelUpdateAgent, PID._BoidsAgentBufferWrite, BoidsAgentBuffer);
        cs.SetBuffer(_kernelUpdateAgent, PID._BoidsForceBufferRead,  BoidsForceBuffer);

        cs.SetBuffer(_kernelBitonicSort, PID._BoidsAgentBufferWrite, BoidsAgentBuffer);
        cs.SetBuffer(_kernelBitonicSort, PID._BoidsAgentBufferRead,  BoidsAgentBuffer);
        cs.SetBuffer(_kernelBitonicSort, PID._AgentIndexBuffer,      AgentIndexBuffer);

        cs.SetBuffer(_kernelUpdateGridIndices, PID._BoidsAgentBufferRead, BoidsAgentBuffer);
        cs.SetBuffer(_kernelUpdateGridIndices, PID._GridIndicesBuffer,    GridIndicesBuffer);
        cs.SetBuffer(_kernelUpdateGridIndices, PID._AgentIndexBuffer,     AgentIndexBuffer);

        // NOTE:
        // Append/Consume buffer must be initialized by ComputeShader.
        boidsComputeShader.SetInt   (PID._MaxAgentCount, maxAgentCount);
        boidsComputeShader.SetBuffer(_kernelInitializePool, PID._PooledAgentBufferAppend, PooledAgentBuffer);
        boidsComputeShader.Dispatch (_kernelInitializePool, Mathf.CeilToInt((float)maxAgentCount / _tgsInitializePool.x), 1, 1);
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

        AgentIndexBuffer?.Dispose();
        AgentIndexBuffer = null;

        EmitAgentBuffer?.Dispose();
        EmitAgentBuffer = null;

        GridIndicesBuffer?.Dispose();
        GridIndicesBuffer = null;
    }

    private void Simulate()
    {
        var threadGroups = Vector3Int.one;
        var cs           = boidsComputeShader;

        cs.SetFloat(PID._DeltaTime, Time.deltaTime);

        // Update Status
        threadGroups = GetThreadGroups(boidsParameter.maxAgentCount, _tgsUpdateStatus);
        cs.Dispatch(_kernelUpdateStatus, threadGroups.x, threadGroups.y, threadGroups.z);

        // Emit
        var emitAgentCount = _emitAgentsBuffer.Count;
        if (0 < emitAgentCount)
        {
            threadGroups = GetThreadGroups(emitAgentCount, _tgsEmitAgent);
            EmitAgentBuffer.SetData(_emitAgentsBuffer);

            cs.SetInt(PID._EmitAgentCount, emitAgentCount);
            cs.SetBuffer(_kernelEmitAgent, PID._EmitAgentBuffer, EmitAgentBuffer);
            cs.Dispatch (_kernelEmitAgent, threadGroups.x, threadGroups.y, threadGroups.z);
        }

        // Update Agents
        threadGroups = GetThreadGroups(boidsParameter.maxAgentCount, _tgsUpdateAgent);
        cs.Dispatch(_kernelUpdateAgent, threadGroups.x, threadGroups.y, threadGroups.z);

        // Sort
        var agentCount     = boidsParameter.maxAgentCount;
        var numElement     = (uint)agentCount;
        var log2NumElement = 0;
        var temp           = numElement;

        while (1 < temp)
        {
            temp >>= 1; // means temp /= 2
            log2NumElement++;
        }

        var paddedSize = 1u << log2NumElement; // means 2 ^ log2NumElement

        if (paddedSize < numElement)
        {
            paddedSize <<= 1; // means paddedSize *= 2
        }

        cs.SetInt(PID._AgentCount, agentCount);

        for (var k = 2; k <= paddedSize; k <<= 1) // means k *= 2 (block width)
        {
            threadGroups = GetThreadGroups((int)paddedSize, _tgsBitonicSort);

            for (var j = k >> 1; j > 0; j >>= 1) // means j /= 2 (comparison distance)
            {
                cs.SetInt(PID._SortBlockSize,  j);
                cs.SetInt(PID._SortBlockWidth, k);
                cs.Dispatch(_kernelBitonicSort, threadGroups.x, threadGroups.y, threadGroups.z);
            }
        }

        // Update Indices
        threadGroups = GetThreadGroups(agentCount, _tgsUpdateGridIndices);
        cs.Dispatch(_kernelUpdateGridIndices, threadGroups.x, threadGroups.y, threadGroups.z);

        // Update Force
        threadGroups = GetThreadGroups(boidsParameter.maxAgentCount, _tgsUpdateForce);
        cs.Dispatch(_kernelUpdateForce, threadGroups.x, threadGroups.y, threadGroups.z);

        return;

        Vector3Int GetThreadGroups(int dataCount, Vector3Int threadGroupSize)
        {
            return new Vector3Int(Mathf.CeilToInt((float)dataCount / threadGroupSize.x), 1, 1);
        }
    }
}}