using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using Ryujinx.Common.Logging;

namespace Ryujinx.Graphics.Vulkan
{
    sealed class BackgroundCompilationScheduler : IDisposable
    {
        private readonly WorkerQueue _shaderQueue;
        private readonly WorkerQueue _pipelineQueue;
        private readonly PipelineCacheManager _pipelineCacheManager;

        public static int PipelineWorkerCount => GetBackgroundCompileThreadCount();
        public static int PipelineCacheCount => PipelineWorkerCount + 1;

        public BackgroundCompilationScheduler(PipelineCacheManager pipelineCacheManager)
        {
            _pipelineCacheManager = pipelineCacheManager;
            _shaderQueue = new("Shader", GetBackgroundCompileThreadCount());
            _pipelineQueue = new("Pipeline", PipelineWorkerCount);
        }

        private static int GetBackgroundCompileThreadCount()
        {
            return Math.Max(1, Math.Min(2, Environment.ProcessorCount / 4));
        }

        public Task ScheduleShaderCompile(Action action, bool highPriority = false)
        {
            return _shaderQueue.Schedule(_ => action(), highPriority);
        }

        public Task SchedulePipelineCompile(Action<PipelineCache> action, bool highPriority = false, bool demanded = false)
        {
            // Signal readiness before optional cache I/O. Maintenance remains on
            // the owning worker, before its next job, for external synchronization.
            return _pipelineQueue.Schedule(
                workerIndex => action(_pipelineCacheManager.GetWorkerCache(workerIndex)),
                highPriority,
                demanded,
                workerIndex => _pipelineCacheManager.NotifyWorkerPipelineCreated(
                    workerIndex, allowCheckpoint: !_pipelineQueue.HasPendingDemand));
        }

        public void PromoteShaderCompile(Task task)
        {
            _shaderQueue.Promote(task);
        }

        public void PromotePipelineCompile(Task task)
        {
            _pipelineQueue.Promote(task);
        }

        public void Dispose()
        {
            _shaderQueue.Dispose();
            _pipelineQueue.Dispose();
        }

        private sealed class WorkerQueue : IDisposable
        {
            private sealed class WorkItem
            {
                private readonly Action<int> _action;
                private readonly Action<int> _afterCompletion;

                public TaskCompletionSource<bool> Completion { get; }
                public LinkedListNode<WorkItem> Node { get; set; }
                public bool Demanded { get; set; }

                public WorkItem(Action<int> action, Action<int> afterCompletion)
                {
                    _action = action;
                    _afterCompletion = afterCompletion;
                    Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                public void Execute(int workerIndex)
                {
                    try
                    {
                        _action(workerIndex);
                        Completion.TrySetResult(true);
                    }
                    catch (Exception exception)
                    {
                        Completion.TrySetException(exception);
                    }
                    finally
                    {
                        try
                        {
                            _afterCompletion?.Invoke(workerIndex);
                        }
                        catch (Exception exception)
                        {
                            Logger.Warning?.PrintMsg(LogClass.Gpu,
                                $"Background compilation maintenance failed: {exception}");
                        }
                    }
                }

                public void Cancel(Exception exception)
                {
                    Completion.TrySetException(exception);
                }
            }

            private readonly object _queueLock = new();
            private readonly LinkedList<WorkItem> _demandQueue = new();
            private readonly LinkedList<WorkItem> _highPriorityQueue = new();
            private readonly LinkedList<WorkItem> _lowPriorityQueue = new();
            private readonly Dictionary<Task, WorkItem> _scheduled = [];
            private readonly Thread[] _threads;
            private readonly string _name;

            private bool _stopping;

            public bool HasPendingDemand
            {
                get
                {
                    lock (_queueLock)
                    {
                        return _demandQueue.Count != 0;
                    }
                }
            }

            public WorkerQueue(string name, int workerCount)
            {
                _name = name;
                _threads = new Thread[workerCount + 1];

                for (int index = 0; index < workerCount; index++)
                {
                    int workerIndex = index;

                    _threads[index] = new Thread(() => WorkerLoop(workerIndex, demandOnly: false))
                    {
                        IsBackground = true,
                        Name = $"Vulkan {name} Compile {index}",
                        Priority = ThreadPriority.BelowNormal,
                    };
                }

                
                _threads[workerCount] = new Thread(() => WorkerLoop(workerCount, demandOnly: true))
                {
                    IsBackground = true,
                    Name = $"Vulkan {name} Demand Compile",
                    Priority = ThreadPriority.Normal,
                };

                foreach (Thread thread in _threads)
                {
                    thread.Start();
                }
            }

            public Task Schedule(Action<int> action, bool highPriority, bool demanded = false, Action<int> afterCompletion = null)
            {
                WorkItem item = new(action, afterCompletion);

                lock (_queueLock)
                {
                    if (_stopping)
                    {
                        item.Cancel(new ObjectDisposedException($"{_name} background compilation queue"));

                        return item.Completion.Task;
                    }

                    _scheduled.Add(item.Completion.Task, item);

                    Enqueue(item, demanded, highPriority);

                    Monitor.PulseAll(_queueLock);
                }

                return item.Completion.Task;
            }

            public void Promote(Task task)
            {
                if (task == null)
                {
                    return;
                }

                lock (_queueLock)
                {
                    if (_stopping ||
                        !_scheduled.TryGetValue(task, out WorkItem item) ||
                        item.Demanded ||
                        item.Node == null)
                    {
                        return;
                    }

                    item.Node.List.Remove(item.Node);

                    Enqueue(item, demanded: true, highPriority: true);

                    Monitor.PulseAll(_queueLock);
                }
            }

            private void Enqueue(WorkItem item, bool demanded, bool highPriority)
            {
                LinkedList<WorkItem> queue = demanded
                    ? _demandQueue
                    : highPriority
                        ? _highPriorityQueue
                        : _lowPriorityQueue;

                item.Demanded = demanded;
                item.Node = queue.AddLast(item);
            }

            private void WorkerLoop(int workerIndex, bool demandOnly)
            {
                while (true)
                {
                    WorkItem item;

                    lock (_queueLock)
                    {
                        while ((item = TryTake(demandOnly)) == null)
                        {
                            if (_stopping)
                            {
                                return;
                            }

                            Monitor.Wait(_queueLock);
                        }
                    }

                    item.Execute(workerIndex);

                    lock (_queueLock)
                    {
                        _scheduled.Remove(item.Completion.Task);
                    }
                }
            }

            private WorkItem TryTake(bool demandOnly)
            {
                LinkedListNode<WorkItem> node = _demandQueue.First;

                if (node == null && !demandOnly)
                {
                    node = _highPriorityQueue.First ?? _lowPriorityQueue.First;
                }

                if (node == null)
                {
                    return null;
                }

                node.List.Remove(node);

                WorkItem item = node.Value;
                item.Node = null;

                return item;
            }

            public void Dispose()
            {
                List<WorkItem> pending = [];

                lock (_queueLock)
                {
                    if (_stopping)
                    {
                        return;
                    }

                    _stopping = true;

                    Drain(_demandQueue, pending);
                    Drain(_highPriorityQueue, pending);
                    Drain(_lowPriorityQueue, pending);

                    _scheduled.Clear();

                    Monitor.PulseAll(_queueLock);
                }

                if (pending.Count != 0)
                {
                    ObjectDisposedException exception = new($"{_name} background compilation queue");

                    foreach (WorkItem item in pending)
                    {
                        item.Cancel(exception);
                    }
                }

                foreach (Thread thread in _threads)
                {
                    thread.Join();
                }
            }

            private static void Drain(LinkedList<WorkItem> queue, List<WorkItem> pending)
            {
                while (queue.First != null)
                {
                    WorkItem item = queue.First.Value;

                    queue.RemoveFirst();
                    item.Node = null;
                    pending.Add(item);
                }
            }
        }
    }
}
