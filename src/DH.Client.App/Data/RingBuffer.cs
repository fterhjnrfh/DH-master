using System;
using System.Collections.Generic;

namespace DH.Client.App.Data
{
    public class RingBuffer<T>
    {
        private T[] _buffer;
        private int _head;
        private int _tail;
        private int _size;
        private int _capacity;
        private readonly bool _allowExpand;
        private readonly object _syncLock = new();

        public RingBuffer(int capacity, bool allowExpand = false)
        {
            if (capacity <= 0) capacity = 1024;
            _allowExpand = allowExpand;
            _capacity = capacity;
            _buffer = new T[capacity];
            _head = 0;
            _tail = 0;
            _size = 0;
        }

        public int Count
        {
            get
            {
                lock (_syncLock)
                {
                    return _size;
                }
            }
        }

        public int Capacity => _capacity;

        public void Add(T item)
        {
            lock (_syncLock)
            {
                if (_size == _capacity)
                {
                    if (_allowExpand)
                    {
                        int newCap = Math.Max(16, _capacity * 2);
                        var newBuf = new T[newCap];
                        if (_size > 0)
                        {
                            int idx = 0;
                            for (int i = 0; i < _size; i++)
                            {
                                newBuf[idx++] = _buffer[(_head + i) % _capacity];
                            }
                        }
                        _buffer = newBuf;
                        _capacity = newCap;
                        _head = 0;
                        _tail = _size;
                    }
                    else
                    {
                        _head = (_head + 1) % _capacity;
                    }
                }

                _buffer[_tail] = item;
                _tail = (_tail + 1) % _capacity;
                if (_size < _capacity) _size++;
            }
        }

        public void AddRange(IEnumerable<T> items)
        {
            if (items == null)
                throw new ArgumentNullException(nameof(items));

            lock (_syncLock)
            {
                foreach (var item in items)
                {
                    if (_size == _capacity)
                    {
                        if (_allowExpand)
                        {
                            int newCap = Math.Max(16, _capacity * 2);
                            var newBuf = new T[newCap];
                            if (_size > 0)
                            {
                                int idx = 0;
                                for (int i = 0; i < _size; i++)
                                {
                                    newBuf[idx++] = _buffer[(_head + i) % _capacity];
                                }
                            }

                            _buffer = newBuf;
                            _capacity = newCap;
                            _head = 0;
                            _tail = _size;
                        }
                        else
                        {
                            _head = (_head + 1) % _capacity;
                        }
                    }

                    _buffer[_tail] = item;
                    _tail = (_tail + 1) % _capacity;
                    if (_size < _capacity)
                    {
                        _size++;
                    }
                }
            }
        }

        public T[] GetLatest(int count)
        {
            lock (_syncLock)
            {
                if (count <= 0)
                    return Array.Empty<T>();

                int actualCount = Math.Min(count, _size);
                T[] result = new T[actualCount];

                if (actualCount == 0)
                    return result;

                int startIndex = (_tail - actualCount + _capacity) % _capacity;

                for (int i = 0; i < actualCount; i++)
                {
                    result[i] = _buffer[(startIndex + i) % _capacity];
                }

                return result;
            }
        }

        public T[] GetAll()
        {
            lock (_syncLock)
            {
                T[] result = new T[_size];
                if (_size == 0)
                    return result;

                int startIndex = _head;
                for (int i = 0; i < _size; i++)
                {
                    result[i] = _buffer[(startIndex + i) % _capacity];
                }

                return result;
            }
        }

        public void Clear()
        {
            lock (_syncLock)
            {
                _head = 0;
                _tail = 0;
                _size = 0;
                Array.Clear(_buffer, 0, _capacity);
            }
        }
    }
}
