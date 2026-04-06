using System;

namespace Flow.Grains.Plan
{
    [Flags]
    public enum StreamFlags
    {
        Create = 1,
        Resume = 2
    }
}
