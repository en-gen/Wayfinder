using System;

namespace Wayfinder.Grains.Plan
{
    [Flags]
    public enum StreamFlags
    {
        Create = 1,
        Resume = 2
    }
}
