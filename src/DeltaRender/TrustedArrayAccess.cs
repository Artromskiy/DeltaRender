using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Delta.Render;

internal static class TrustedArrayAccess
{
    // Callers must prove non-null storage and valid indices before using these accessors.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ref T DataReference<T>(T[] array) =>
        ref MemoryMarshal.GetArrayDataReference(array);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ref T DataReference<T>(Span<T> span) =>
        ref MemoryMarshal.GetReference(span);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ref readonly T DataReference<T>(ReadOnlySpan<T> span) =>
        ref MemoryMarshal.GetReference(span);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ref T RefAt<T>(T[] array, int index) =>
        ref Unsafe.Add(ref DataReference(array), index);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ref T RefAt<T>(Span<T> span, int index) =>
        ref Unsafe.Add(ref DataReference(span), index);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ref readonly T RefAt<T>(ReadOnlySpan<T> span, int index)
    {
        ref readonly var first = ref DataReference(span);
        return ref Unsafe.Add(ref Unsafe.AsRef(in first), index);
    }
}
