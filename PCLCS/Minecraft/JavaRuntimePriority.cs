// SPDX-License-Identifier: MPL-2.0
using MeloongCore;

namespace PCLCS;

public static class JavaRuntimePriority
{
    public static void Move(Java entry, int offset)
    {
        lock (Configs.JavaList)
        {
            Configs.JavaList.Edit((ref ConcurrentList<Java>? values) =>
            {
                if (values is null) return;
                int index = values.IndexOf(entry);
                int next = checked(index + offset);
                if (index < 0 || next < 0 || next >= values.Count) return;
                values.RemoveAt(index);
                values.Insert(next, entry);
            });
        }
    }
}
