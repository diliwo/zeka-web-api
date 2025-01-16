using System.Collections;

namespace Domain.UnitTests.Common;

public class DateTimeProviderContext
{
    internal DateTime ContextDateTimeNow;
    private static ThreadLocal<Stack> ThreadScopeStack = new ThreadLocal<Stack>(() => new Stack());
    private Stack _contextStack = new Stack();

    public DateTimeProviderContext(DateTime contextDateTimeNow)
    {
        ContextDateTimeNow = contextDateTimeNow;
        ThreadScopeStack.Value.Push(this);
    }

    public static DateTimeProviderContext Current
    {
        get
        {
            if (ThreadScopeStack.Value.Count == 0)
                return null;
            else
                return ThreadScopeStack.Value.Peek() as DateTimeProviderContext;
        }
    }

    public void Dispose()
    {
        ThreadScopeStack.Value.Pop();
    }
}