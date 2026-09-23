// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.Internal;

/// <summary>
/// One node in the zero-copy <see cref="ReadOnlySequence{Byte}"/> that
/// <see cref="IoUringPipeReader"/> hands to the application: exactly one completed
/// multishot-recv provided-buffer, in arrival order.
/// </summary>
internal sealed class IoUringBufferSegment : ReadOnlySequenceSegment<byte>
{
    private bool _provided = true;

    public IMemoryOwner<byte> Owner { get; private set; } = null!;

    public long Start => RunningIndex;

    public IoUringBufferSegment(IMemoryOwner<byte> owner, long runningIndex)
    {
        Initialize(owner, runningIndex);
    }

    public void Initialize(IMemoryOwner<byte> owner, long runningIndex)
    {
        _provided = true;
        Owner = owner;
        Memory = owner.Memory;
        RunningIndex = runningIndex;
        Next = null;
    }

    public void SetNext(IoUringBufferSegment? next) => Next = next;

    public void Reset()
    {
        Owner.Dispose();
        Owner = null!;
        Memory = default;
        Next = null;
    }

    public void ReleaseProvidedBuffer(MemoryPool<byte> pool)
    {
        if (!_provided)
        {
            return;
        }

        IMemoryOwner<byte> replacement = pool.Rent(Memory.Length);
        Memory<byte> memory = replacement.Memory[..Memory.Length];
        Memory.CopyTo(memory);
        Owner.Dispose();
        Owner = replacement;
        Memory = memory;
        _provided = false;
    }
}
