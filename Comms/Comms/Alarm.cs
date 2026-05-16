using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Comms;

public class Alarm : IDisposable
{
    private static Task Task;

    private static AutoResetEvent WaitEvent;

    private static LinkedList<Alarm> Alarms;

    private volatile bool IsDisposed;

    private LinkedListNode<Alarm> Node;

    private Action Handler;

    private double DueTime;

    public event Action<Exception> Error;

    static Alarm()
    {
        WaitEvent = new AutoResetEvent(false);
        Alarms = new LinkedList<Alarm>();
        Task = new Task(TaskFunction, TaskCreationOptions.LongRunning);
        Task.Start();
    }

    public Alarm(Action handler)
    {
        Node = new LinkedListNode<Alarm>(this);
        Handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public void Dispose()
    {
        lock (Alarms)
        {
            IsDisposed = true;
        }
    }

    public void Set(double delay)
    {
        lock (Alarms)
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException("Alarm");
            }
            if (delay < 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(delay));
            }
            if (delay == 0.0)
            {
                if (Node.List != null)
                {
                    Alarms.Remove(Node);
                }
                Task.Run((Action)AlarmFunction);
                return;
            }
            DueTime = Comm.GetTime() + delay;
            if (Node.List == null)
            {
                if (DueTime != 1.0 / 0.0)
                {
                    Alarms.AddLast(Node);
                }
            }
            else if (DueTime == 1.0 / 0.0)
            {
                Alarms.Remove(Node);
            }
            WaitEvent.Set();
        }
    }

    private void AlarmFunction()
    {
        try
        {
            Handler();
        }
        catch (Exception obj)
        {
            this.Error?.Invoke(obj);
        }
    }

    private static void TaskFunction()
    {
        Thread.CurrentThread.Name = "Alarm";
        while (true)
        {
            double num = 1.7976931348623157E+308;
            lock (Alarms)
            {
                double time = Comm.GetTime();
                LinkedListNode<Alarm> linkedListNode = Alarms.First;
                while (linkedListNode != null)
                {
                    LinkedListNode<Alarm>? next = linkedListNode.Next;
                    Alarm value = linkedListNode.Value;
                    if (value.IsDisposed)
                    {
                        Alarms.Remove(linkedListNode);
                    }
                    else
                    {
                        double num2 = value.DueTime - time;
                        if (num2 <= 0.0)
                        {
                            Alarms.Remove(linkedListNode);
                            Task.Run((Action)value.AlarmFunction);
                        }
                        else
                        {
                            num = Math.Min(num, num2);
                        }
                    }
                    linkedListNode = next;
                }
            }
            if (num < 1.7976931348623157E+308)
            {
                WaitEvent.WaitOne((int)(Math.Min(num, 60.0) * 1000.0));
            }
            else
            {
                WaitEvent.WaitOne();
            }
        }
    }
}
