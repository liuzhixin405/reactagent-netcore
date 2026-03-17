using System.Collections.Concurrent;

namespace AiCli.A2aServer.A2a;

/// <summary>
/// In-memory store for A2A tasks.
/// </summary>
public class A2aTaskStore
{
    private readonly ConcurrentDictionary<string, A2aTask> _tasks = new();

    public void Save(A2aTask task)
    {
        _tasks[task.Id] = task;
    }

    public A2aTask? Load(string taskId)
    {
        _tasks.TryGetValue(taskId, out var task);
        return task;
    }

    public IReadOnlyList<A2aTask> GetAll()
    {
        return _tasks.Values.ToList();
    }

    public bool Remove(string taskId)
    {
        return _tasks.TryRemove(taskId, out _);
    }
}
