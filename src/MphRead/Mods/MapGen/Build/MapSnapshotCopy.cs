using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;

namespace MphRead.Mods.MapGen;

/// <summary>Detached DTO copies without JSON encoding on the editor dispatcher.
/// Only authoring data is supported; renderer/compiler resources never cross this boundary.</summary>
internal static class MapSnapshotCopy
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Properties = new();
    internal static T Copy<T>(T value) => (T)CopyValue(value)!;
    private static object? CopyValue(object? value)
    {
        if (value == null) return null;
        Type type = value.GetType();
        if (type.IsValueType || value is string) return value;
        if (value is Array array)
        {
            Array result = (Array)array.Clone();
            if (!type.GetElementType()!.IsValueType && type.GetElementType() != typeof(string))
                for (int i = 0; i < result.Length; i++) result.SetValue(CopyValue(array.GetValue(i)), i);
            return result;
        }
        if (value is IList list)
        {
            var result = (IList)Activator.CreateInstance(type)!;
            foreach (object? item in list) result.Add(CopyValue(item));
            return result;
        }
        if (value is IDictionary dictionary)
        {
            var result = (IDictionary)Activator.CreateInstance(type)!;
            foreach (DictionaryEntry item in dictionary) result.Add(item.Key, CopyValue(item.Value));
            return result;
        }
        if (type.Namespace != typeof(MapDefinition).Namespace)
            throw new InvalidOperationException("Unsupported map snapshot data: " + type.FullName);
        object copy = Activator.CreateInstance(type)!;
        foreach (PropertyInfo property in Properties.GetOrAdd(type, t => t.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanRead && p.SetMethod?.IsPublic == true && p.GetIndexParameters().Length == 0).ToArray()))
            property.SetValue(copy, CopyValue(property.GetValue(value)));
        return copy;
    }
}
