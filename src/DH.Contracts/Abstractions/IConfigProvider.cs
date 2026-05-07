namespace DH.Contracts.Abstractions;

public interface IConfigProvider
{
    T Get<T>(string section) where T : new();
}