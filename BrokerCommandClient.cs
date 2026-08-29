using System;
using System.Collections.Generic;
using System.Reflection;

namespace LocalMultiplayerMod
{
    internal static class BrokerCommandClient
    {
        private const string RegistryTypeName =
            "JumpKingHttpCommandBroker.CommandQueueRegistry";

        private static object _registry;
        private static MethodInfo _registerMethod;
        private static MethodInfo _tryDequeueMethod;
        private static int _lastResolveAssemblyCount = -1;
        private static bool _registered;
        private static bool _loggedMissingBroker;

        public static void Register(string target)
        {
            if (_registered || !Resolve())
            {
                return;
            }

            try
            {
                _registerMethod.Invoke(_registry, new object[] { target });
                _registered = true;
            }
            catch (Exception ex)
            {
                NetplayLog.Write(
                    "Local Multiplayer broker register failed: " + ex.Message
                );
            }
        }

        public static bool TryDequeue(
            string target,
            out IReadOnlyDictionary<string, string> parameters
        )
        {
            parameters = null;
            if (!_registered)
            {
                Register(target);
            }

            if (!_registered)
            {
                return false;
            }

            try
            {
                object[] args = new object[] { target, null };
                bool dequeued = (bool)_tryDequeueMethod.Invoke(_registry, args);
                parameters = args[1] as IReadOnlyDictionary<string, string>;
                return dequeued;
            }
            catch (Exception ex)
            {
                NetplayLog.Write(
                    "Local Multiplayer broker dequeue failed: " + ex.Message
                );
                return false;
            }
        }

        private static bool Resolve()
        {
            if (_registry != null)
            {
                return true;
            }

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            if (_lastResolveAssemblyCount == assemblies.Length)
            {
                return false;
            }

            _lastResolveAssemblyCount = assemblies.Length;
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type registryType = assemblies[i].GetType(
                    RegistryTypeName,
                    false
                );
                if (registryType == null)
                {
                    continue;
                }

                FieldInfo instanceField = registryType.GetField(
                    "Instance",
                    BindingFlags.Public | BindingFlags.Static
                );
                MethodInfo registerMethod = registryType.GetMethod(
                    "Register",
                    new Type[] { typeof(string) }
                );
                MethodInfo tryDequeueMethod = registryType.GetMethod(
                    "TryDequeue",
                    new Type[]
                    {
                        typeof(string),
                        typeof(IReadOnlyDictionary<string, string>).MakeByRefType()
                    }
                );

                if (instanceField == null ||
                    registerMethod == null ||
                    tryDequeueMethod == null)
                {
                    continue;
                }

                _registry = instanceField.GetValue(null);
                _registerMethod = registerMethod;
                _tryDequeueMethod = tryDequeueMethod;
                return _registry != null;
            }

            if (!_loggedMissingBroker)
            {
                _loggedMissingBroker = true;
                NetplayLog.Write(
                    "Local Multiplayer: JumpKingHttpCommandBroker is not loaded."
                );
            }

            return false;
        }
    }
}
