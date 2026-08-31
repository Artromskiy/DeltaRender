namespace Delta.Render.Vulkan;

internal sealed class VulkanHeadlessFrameSlots
{
    private readonly ulong[] _slotFrameNumbers;
    private int _nextSlot;

    public VulkanHeadlessFrameSlots(int count)
    {
        _slotFrameNumbers = new ulong[count];
    }

    public int Count => _slotFrameNumbers.Length;

    public int CurrentIndex { get; private set; } = -1;

    public ulong CurrentFrameNumber => CurrentIndex < 0 ? 0 : _slotFrameNumbers[CurrentIndex];

    public int Advance()
    {
        CurrentIndex = _nextSlot;
        _nextSlot = (_nextSlot + 1) % _slotFrameNumbers.Length;
        _slotFrameNumbers[CurrentIndex]++;
        return CurrentIndex;
    }

    public void Reset()
    {
        _nextSlot = 0;
        CurrentIndex = -1;
        Array.Clear(_slotFrameNumbers);
    }
}
