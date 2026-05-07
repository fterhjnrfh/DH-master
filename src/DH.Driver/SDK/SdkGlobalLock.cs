// DH.Driver/SDK/SdkGlobalLock.cs
// SDK全局锁定机制 - 防止多个模块同时使用SDK

using System;

namespace DH.Driver.SDK;

/// <summary>
/// SDK全局锁定管理器
/// 确保同一时间只有一个模块可以使用SDK
/// </summary>
public static class SdkGlobalLock
{
    private static readonly object _lock = new();
    private static string? _currentOwner;
    private static bool _isLocked;
    
    /// <summary>
    /// SDK是否已被锁定
    /// </summary>
    public static bool IsLocked
    {
        get
        {
            lock (_lock)
            {
                return _isLocked;
            }
        }
    }
    
    /// <summary>
    /// 当前锁定SDK的模块名称
    /// </summary>
    public static string? CurrentOwner
    {
        get
        {
            lock (_lock)
            {
                return _currentOwner;
            }
        }
    }
    
    /// <summary>
    /// 尝试获取SDK锁
    /// </summary>
    /// <param name="owner">请求锁的模块名称</param>
    /// <returns>是否成功获取锁</returns>
    public static bool TryAcquire(string owner)
    {
        lock (_lock)
        {
            if (_isLocked && _currentOwner != owner)
            {
                Console.WriteLine($"[SdkGlobalLock] SDK已被 '{_currentOwner}' 占用，'{owner}' 无法获取");
                return false;
            }
            
            _isLocked = true;
            _currentOwner = owner;
            Console.WriteLine($"[SdkGlobalLock] '{owner}' 获取了SDK锁");
            return true;
        }
    }
    
    /// <summary>
    /// 释放SDK锁
    /// </summary>
    /// <param name="owner">释放锁的模块名称</param>
    /// <returns>是否成功释放</returns>
    public static bool Release(string owner)
    {
        lock (_lock)
        {
            if (!_isLocked)
            {
                return true;
            }
            
            if (_currentOwner != owner)
            {
                Console.WriteLine($"[SdkGlobalLock] '{owner}' 无法释放由 '{_currentOwner}' 持有的锁");
                return false;
            }
            
            _isLocked = false;
            _currentOwner = null;
            Console.WriteLine($"[SdkGlobalLock] '{owner}' 释放了SDK锁");
            return true;
        }
    }
    
    /// <summary>
    /// 强制释放SDK锁（用于异常情况）
    /// </summary>
    public static void ForceRelease()
    {
        lock (_lock)
        {
            Console.WriteLine($"[SdkGlobalLock] 强制释放SDK锁（原持有者: '{_currentOwner}'）");
            _isLocked = false;
            _currentOwner = null;
        }
    }
    
    /// <summary>
    /// SDK锁定状态变化事件
    /// </summary>
    public static event EventHandler<SdkLockChangedEventArgs>? LockChanged;
    
    internal static void RaiseLockChanged(bool isLocked, string? owner)
    {
        LockChanged?.Invoke(null, new SdkLockChangedEventArgs(isLocked, owner));
    }
}

/// <summary>
/// SDK锁定状态变化事件参数
/// </summary>
public class SdkLockChangedEventArgs : EventArgs
{
    public bool IsLocked { get; }
    public string? Owner { get; }
    
    public SdkLockChangedEventArgs(bool isLocked, string? owner)
    {
        IsLocked = isLocked;
        Owner = owner;
    }
}
