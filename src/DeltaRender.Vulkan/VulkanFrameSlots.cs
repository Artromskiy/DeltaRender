namespace Delta.Render.Vulkan;

internal sealed class VulkanFrameSlots
{
    private readonly ulong[] _slotFrameNumbers;
    private int _nextSlot;

    public VulkanFrameSlots(int count)
    {
        _slotFrameNumbers = new ulong[count];
    }

    public int Count => _slotFrameNumbers.Length;

    public int CurrentIndex { get; private set; } = -1;

    public ulong CurrentFrameNumber => CurrentIndex < 0 ? 0 : _slotFrameNumbers[CurrentIndex];

    public int Advance()
    {
        CurrentIndex = _nextSlot;
        var nextSlot = _nextSlot + 1;
        if (nextSlot == _slotFrameNumbers.Length)
        {
            nextSlot = 0;
        }

        _nextSlot = nextSlot;
        _slotFrameNumbers[CurrentIndex]++;
        return CurrentIndex;
    }

    public void Reset()
    {
        Array.Clear(_slotFrameNumbers);
        _nextSlot = 0;
        CurrentIndex = -1;
    }
}
