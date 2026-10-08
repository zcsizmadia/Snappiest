namespace SnappySimd.Internal;

/// <summary>
/// Runs equal-sized work items (64KB fragments or chunks) on several threads, handing out one index at a time.
/// </summary>
/// <remarks>
/// Parallel.For partitions indices into growing ranges, which balances long loops well, but with a batch of only
/// a few items per thread one worker can take three or four while others get one, and the whole batch waits for
/// it (measured: stream compression with 12 threads slower than with 8). <see cref="Start"/> runs the workers as
/// pool tasks so the calling thread can do other work (write a stream, copy decoded chunks out) before it joins
/// them as the last worker. Like Parallel.For, each worker queues the next one when it starts running, so at most
/// one is ever waiting for a pool thread, and the join only waits for workers that started: one that the pool
/// starts later finds no index left.
/// </remarks>
internal sealed class ParallelWork
{
    private readonly int _count;
    private readonly Action<int> _body;
    private readonly Task[] _tasks;
    private readonly int[] _started;
    private int _next = -1;
    private int _stop; // 1 once the work should stop handing out indices

    private ParallelWork(int count, int threads, Action<int> body)
    {
        _count = count;
        _body = body;

        // The calling thread is one of the workers
        int workers = Math.Max(0, Math.Min(count, threads) - 1);
        _tasks = new Task[workers];
        _started = new int[workers];
        for (int i = 0; i < workers; i++)
        {
            int worker = i;
            _tasks[i] = new Task(() => Work(worker), TaskCreationOptions.DenyChildAttach);
        }

        StartWorker(0);
    }

    private void StartWorker(int worker)
    {
        if (worker < _tasks.Length && Interlocked.Exchange(ref _started[worker], 1) == 0)
        {
            _tasks[worker].Start(TaskScheduler.Default);
        }
    }

    private void Work(int worker)
    {
        StartWorker(worker + 1);
        Loop();
    }

    /// <summary>
    /// Calls <paramref name="body"/> for every index in [0, <paramref name="count"/>) on up to
    /// <paramref name="threads"/> threads, including the calling one, and returns when all are done.
    /// </summary>
    /// <remarks>
    /// Parallel.For, not <see cref="Start"/> and <see cref="Join"/>: with the pool workers started by this class,
    /// about every second process settled into a state where 16-thread batches of a few dozen chunks took twice as
    /// long (workers arriving late), while Parallel.For's replicas never did in the same runs.
    /// </remarks>
    /// <exception cref="AggregateException">A call threw.</exception>
    public static void For(int count, int threads, Action<int> body)
    {
        int next = -1;
        bool stop = false;
        int workers = Math.Min(count, threads);
        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, _ =>
        {
            // Parallel.For cannot interrupt a running worker, so the loop checks a flag set by a failing call
            int index;
            while (!Volatile.Read(ref stop) && (index = Interlocked.Increment(ref next)) < count)
            {
                try
                {
                    body(index);
                }
                catch
                {
                    Volatile.Write(ref stop, true);
                    throw;
                }
            }
        });
    }

    /// <summary>
    /// Starts <paramref name="threads"/> - 1 pool workers; the caller must <see cref="Join"/> before touching the
    /// work's data again.
    /// </summary>
    public static ParallelWork Start(int count, int threads, Action<int> body) => new(count, threads, body);

    /// <summary>Works until every index is taken, then waits for the other workers.</summary>
    /// <exception cref="AggregateException">A call threw. Every worker has stopped.</exception>
    public void Join()
    {
        Exception? error = null;
        try
        {
            Loop();
        }
        catch (Exception exception)
        {
            error = exception;
        }

        try
        {
            WaitForWorkers();
        }
        catch (AggregateException exception) when (error is not null)
        {
            throw new AggregateException([error, .. exception.InnerExceptions]);
        }

        if (error is not null)
        {
            throw new AggregateException(error);
        }
    }

    /// <summary>Waits for the other workers without observing their result, when the work's result is discarded.</summary>
    public void Abandon()
    {
        // A full fence, not a volatile write: WaitForWorkers then reads the workers' started flags, and a store
        // followed by a load can be reordered. A worker started after that read must see the stop flag (its start
        // is fenced too), otherwise it could run an index after Abandon returned and the buffers were released.
        Interlocked.Exchange(ref _stop, 1);
        try
        {
            WaitForWorkers();
        }
        catch (AggregateException)
        {
            // Reported by the first Join of the work; the caller is already failing
        }
    }

    private void WaitForWorkers()
    {
        // Only started workers can hold an index: every index was handed out before this (the caller's loop ran
        // out), so a worker the pool starts later finds nothing to do. Waiting for the queued ones instead cost up
        // to several batch times when the pool was slow to start them (measured: 16-thread decoding 2x slower).
        List<Exception>? errors = null;
        for (int i = 0; i < _tasks.Length; i++)
        {
            if (Volatile.Read(ref _started[i]) == 0)
            {
                continue;
            }

            try
            {
                _tasks[i].Wait();
            }
            catch (AggregateException exception)
            {
                (errors ??= []).AddRange(exception.InnerExceptions);
            }
        }

        if (errors is not null)
        {
            throw new AggregateException(errors);
        }
    }

    private void Loop()
    {
        int index;
        while (Volatile.Read(ref _stop) == 0 && (index = Interlocked.Increment(ref _next)) < _count)
        {
            try
            {
                _body(index);
            }
            catch
            {
                // Let the other workers stop at their next index, as Parallel.For does
                Volatile.Write(ref _stop, 1);
                throw;
            }
        }
    }
}

/// <summary>
/// Moves items compressed into fixed-size slots to their final, packed positions in index order. Whichever worker
/// completes an item packs every item whose predecessors are all in place, so packing overlaps with compression
/// instead of running on the calling thread after a batch.
/// </summary>
internal sealed unsafe class SlotPacker
{
    private byte* _slots;
    private int _slotCount;
    private int _slotSize;
    private byte* _output;
    private int _outputLength;

    // Compressed length of each item, -1 until it is compressed
    private readonly int[] _lengths;
    private int _count;

    // Items before _moved are at their final positions, ending at _packed. Only the thread holding _packing changes them.
    private int _packed;
    private int _moved;
    private int _packing;
    private bool _failed;

    public SlotPacker(int capacity)
    {
        _lengths = new int[capacity];
    }

    /// <summary>The end of the packed items, or where the first one goes until any is packed.</summary>
    public int Packed => _packed;

    /// <summary>An item did not fit in the output; packing has stopped.</summary>
    public bool Failed => Volatile.Read(ref _failed);

    /// <summary>
    /// Prepares for <paramref name="count"/> items. Item i is compressed into slot i % <paramref name="slotCount"/>
    /// and packed to <paramref name="output"/>, the first one at <paramref name="start"/>. When the slots are inside
    /// the output, every slot must start at or after the packed end of the items before it (true when the slots are
    /// at least the items' maximum size and start at <paramref name="start"/>).
    /// </summary>
    public void Reset(int count, byte* slots, int slotCount, int slotSize, byte* output, int outputLength, int start)
    {
        _count = count;
        _slots = slots;
        _slotCount = slotCount;
        _slotSize = slotSize;
        _output = output;
        _outputLength = outputLength;
        _packed = start;
        _moved = 0;
        _packing = 0;
        _failed = false;
        _lengths.AsSpan(0, count).Fill(-1);
    }

    /// <summary>The slot to compress item <paramref name="index"/> into.</summary>
    public Span<byte> GetSlot(int index) => new(_slots + ((nint)(index % _slotCount) * _slotSize), _slotSize);

    /// <summary>
    /// Waits until the slot of item <paramref name="index"/> is free: the item that used it before has been packed.
    /// </summary>
    /// <returns><c>false</c> if packing failed, so the item should not be compressed.</returns>
    public bool WaitForSlot(int index)
    {
        if (_slotCount < _count)
        {
            var spinner = new SpinWait();
            while (Volatile.Read(ref _moved) <= index - _slotCount)
            {
                Pack();
                if (Failed)
                {
                    return false;
                }

                spinner.SpinOnce(sleep1Threshold: -1);
            }
        }

        return !Failed;
    }

    /// <summary>Records that item <paramref name="index"/> is in its slot and packs whatever is ready.</summary>
    public void Complete(int index, int length)
    {
        Volatile.Write(ref _lengths[index], length);
        Pack();
    }

    /// <summary>Moves every compressed item whose predecessors are all packed to its final position.</summary>
    public void Pack()
    {
        while (true)
        {
            int next = Volatile.Read(ref _moved);
            if (next >= _count || Volatile.Read(ref _lengths[next]) < 0 || Failed)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _packing, 1, 0) != 0)
            {
                // Another worker is packing; it re-checks after releasing the lock, so this item is not left behind
                return;
            }

            // Re-read under the lock: another worker may have packed further since the check above
            next = _moved;
            int packed = _packed;
            int length;
            while (next < _count && (length = Volatile.Read(ref _lengths[next])) >= 0)
            {
                if (_outputLength - packed < length)
                {
                    Volatile.Write(ref _failed, true);
                    break;
                }

                Buffer.MemoryCopy(_slots + ((nint)(next % _slotCount) * _slotSize), _output + packed, length, length);
                packed += length;
                next++;
            }

            _packed = packed;
            Volatile.Write(ref _moved, next);

            // A full fence: a worker that completed an item while the lock was held either saw it free afterwards
            // or its length is visible to the re-check above
            Interlocked.Exchange(ref _packing, 0);
        }
    }
}
