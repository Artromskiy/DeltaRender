using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Delta.Render;

internal static class TrustedArrayAccess
{
    // Callers must prove non-null storage and valid indices before using these accessors.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ref T DataReference<T>(this T[] array) =>
        ref MemoryMarshal.GetArrayDataReference(array);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ref T DataReference<T>(this Span<T> span) =>
        ref MemoryMarshal.GetReference(span);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ref readonly T DataReference<T>(this ReadOnlySpan<T> span) =>
        ref MemoryMarshal.GetReference(span);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ref T RefAt<T>(this T[] array, int index) =>
        ref Unsafe.Add(ref array.DataReference(), index);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ref T RefAt<T>(this T[] array, uint index) =>
        ref Unsafe.Add(ref array.DataReference(), (int)index);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ref T RefAt<T>(this Span<T> span, int index) =>
        ref Unsafe.Add(ref span.DataReference(), index);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ref readonly T RefAt<T>(this ReadOnlySpan<T> span, int index)
    {
        ref readonly var first = ref span.DataReference();
        return ref Unsafe.Add(ref Unsafe.AsRef(in first), index);
    }
}
