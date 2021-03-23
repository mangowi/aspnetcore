// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading.Tasks;
using static Microsoft.AspNetCore.Internal.LinkerFlags;

namespace Microsoft.AspNetCore.Components
{
    /// <summary>
    /// The state for the components and services of a components application.
    /// </summary>
    public class PersistentComponentState
    {
        private IDictionary<string, ReadOnlySequence<byte>>? _existingState;
        private IDictionary<string, Pipe> _currentState;
        private readonly List<Func<Task>> _registeredCallbacks;

        internal PersistentComponentState(
            IDictionary<string, Pipe> currentState,
            List<Func<Task>> pauseCallbacks)
        {
            _currentState = currentState;
            _registeredCallbacks = pauseCallbacks;
        }

        internal void InitializeExistingState(IDictionary<string, ReadOnlySequence<byte>> existingState)
        {
            if (_existingState != null)
            {
                throw new InvalidOperationException("PersistentComponentState already initialized.");
            }
            _existingState = existingState ?? throw new ArgumentNullException(nameof(existingState));
        }

        /// <summary>
        /// Register a callback to persist the component state when the application is about to be paused.
        /// Registered callbacks can use this opportunity to persist their state so that it can be retrieved when the application resumes.
        /// </summary>
        /// <param name="callback">The callback to invoke when the application is being paused.</param>
        /// <returns>A subscription that can be used to unregister the callback when disposed.</returns>
        public PersistingComponentStateSubscription RegisterOnPersisting(Func<Task> callback)
        {
            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            _registeredCallbacks.Add(callback);

            return new PersistingComponentStateSubscription(_registeredCallbacks, callback);
        }

        /// <summary>
        /// Tries to retrieve the persisted state with the given <paramref name="key"/>.
        /// When the key is present, the state is successfully returned via <paramref name="value"/>
        /// and removed from the <see cref="PersistentComponentState"/>.
        /// </summary>
        /// <param name="key">The key used to persist the state.</param>
        /// <param name="value">The persisted state.</param>
        /// <returns><c>true</c> if the state was found; <c>false</c> otherwise.</returns>
        public bool TryTake(string key, [MaybeNullWhen(false)] out ReadOnlySequence<byte> value)
        {
            if (key is null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (_existingState == null)
            {
                // Services during prerendering might try to access their state upon injection on the page
                // and we don't want to fail in that case.
                // When a service is prerendering there is no state to restore and in other cases the host
                // is responsible for initializing the state before services or components can access it.
                value = default;
                return false;
            }

            if (_existingState.TryGetValue(key, out value))
            {
                _existingState.Remove(key);
                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Persists the serialized state <paramref name="valueWriter"/> for the given <paramref name="key"/>.
        /// </summary>
        /// <param name="key">The key to use to persist the state.</param>
        /// <param name="valueWriter">The state to persist.</param>
        public void Persist(string key, Action<IBufferWriter<byte>> valueWriter)
        {
            if (key is null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (valueWriter is null)
            {
                throw new ArgumentNullException(nameof(valueWriter));
            }

            if (_currentState.ContainsKey(key))
            {
                throw new ArgumentException($"There is already a persisted object under the same key '{key}'");
            }

            var pipe = new Pipe();
            _currentState.Add(key, pipe);
            valueWriter(pipe.Writer);
            pipe.Writer.Complete();
        }

        /// <summary>
        /// Serializes <paramref name="instance"/> as JSON and persists it under the given <paramref name="key"/>.
        /// </summary>
        /// <typeparam name="TValue">The <paramref name="instance"/> type.</typeparam>
        /// <param name="key">The key to use to persist the state.</param>
        /// <param name="instance">The instance to persist.</param>
        public void PersistAsJson<[DynamicallyAccessedMembers(JsonSerialized)] TValue>(string key, TValue instance)
        {
            if (key is null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (key is null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (_currentState.ContainsKey(key))
            {
                throw new ArgumentException($"There is already a persisted object under the same key '{key}'");
            }

            var pipe = new Pipe();
            _currentState.Add(key, pipe);
            JsonSerializer.Serialize(new Utf8JsonWriter(pipe.Writer), instance, JsonSerializerOptionsProvider.Options);
            pipe.Writer.Complete();
        }

        /// <summary>
        /// Tries to retrieve the persisted state as JSON with the given <paramref name="key"/> and deserializes it into an
        /// instance of type <typeparamref name="TValue"/>.
        /// When the key is present, the state is successfully returned via <paramref name="instance"/>
        /// and removed from the <see cref="PersistentComponentState"/>.
        /// </summary>
        /// <param name="key">The key used to persist the instance.</param>
        /// <param name="instance">The persisted instance.</param>
        /// <returns><c>true</c> if the state was found; <c>false</c> otherwise.</returns>
        public bool TryTakeFromJson<[DynamicallyAccessedMembers(JsonSerialized)] TValue>(string key, [MaybeNullWhen(false)] out TValue? instance)
        {
            if (key is null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (TryTake(key, out var data))
            {
                var reader = new Utf8JsonReader(data);
                instance = JsonSerializer.Deserialize<TValue>(ref reader, JsonSerializerOptionsProvider.Options)!;
                return true;
            }
            else
            {
                instance = default(TValue);
                return false;
            }
        }
    }
}