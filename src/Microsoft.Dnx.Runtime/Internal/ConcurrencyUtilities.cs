// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Dnx.Runtime.Internal
{
    public static class ConcurrencyUtilities
    {
#if DNXCORE50
        private static ConcurrentDictionary<string, System.Threading.Semaphore> _nameSemaphore =
            new ConcurrentDictionary<string, System.Threading.Semaphore>();
#endif

        internal static string FilePathToLockName(string filePath)
        {
            // If we use a file path directly as the name of a semaphore,
            // the ctor of semaphore looks for the file and throws an IOException
            // when the file doesn't exist. So we need a conversion from a file path
            // to a unique lock name.
            return $"DNU_RESTORE_{filePath.Replace(Path.DirectorySeparatorChar, '_')}";
        }

        public static void ExecuteWithFileLocked(string filePath, Action<bool> action)
        {
            ExecuteWithFileLocked(filePath, createdNew =>
            {
                action(createdNew);
                return Task.FromResult(1);
            })
            .GetAwaiter().GetResult();
        }

        public async static Task<T> ExecuteWithFileLocked<T>(string filePath, Func<bool, Task<T>> action)
        {
            bool completed = false;
            while (!completed)
            {
                var createdNew = false;
                var fileLock = new Semaphore(initialCount: 0, maximumCount: 1, name: FilePathToLockName(filePath),
                    createdNew: out createdNew);
                try
                {
                    // If this lock is already acquired by another process, wait until we can acquire it
                    if (!createdNew)
                    {
                        var signaled = fileLock.WaitOne(TimeSpan.FromSeconds(5));
                        if (!signaled)
                        {
                            // Timeout and retry
                            continue;
                        }
                    }

                    completed = true;
                    return await action(createdNew);
                }
                finally
                {
                    if (completed)
                    {
                        fileLock.Release();
                    }
                }
            }

            // should never get here
            throw new TaskCanceledException($"Failed to acquire semaphore for file: {filePath}");
        }

#if DNXCORE50
        private class Semaphore
        {
            private readonly System.Threading.Semaphore _semaphore;
            public Semaphore(int initialCount, int maximumCount, string name, out bool createdNew)
            {
                if (RuntimeEnvironmentHelper.IsWindows)
                {
                    _semaphore = new System.Threading.Semaphore(initialCount, maximumCount, name, out createdNew);
                }
                else
                {
                    var createdNewLocal = false;
                    _semaphore = _nameSemaphore.GetOrAdd(
                        name,
                        valueFactory: _ =>
                        {
                            createdNewLocal = true;
                            return new System.Threading.Semaphore(initialCount, maximumCount);
                        });

                    // C# doesn't allow assigning value to an out parameter directly in lambda expression
                    createdNew = createdNewLocal;
                }
            }

            public bool WaitOne(TimeSpan timeout)
            {
                return _semaphore.WaitOne(timeout);
            }

            public int Release()
            {
                return _semaphore.Release();
            }
        }
#endif
    }
}