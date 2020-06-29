﻿// This software is part of the Autofac IoC container
// Copyright © 2011 Autofac Contributors
// https://autofac.org
//
// Permission is hereby granted, free of charge, to any person
// obtaining a copy of this software and associated documentation
// files (the "Software"), to deal in the Software without
// restriction, including without limitation the rights to use,
// copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following
// conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
// OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
// HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
// OTHER DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using Autofac.Core.Resolving.Pipeline;
using Autofac.Util;

namespace Autofac.Core.Registration
{
    /// <summary>
    /// Keeps track of the status of registered services.
    /// </summary>
    internal class DefaultRegisteredServicesTracker : Disposable, IRegisteredServicesTracker
    {
        private readonly Func<Service, IEnumerable<ServiceRegistration>> _registrationAccessor;

        /// <summary>
        /// Keeps track of the status of registered services.
        /// </summary>
        private readonly Dictionary<Service, ServiceRegistrationInfo> _serviceInfo = new Dictionary<Service, ServiceRegistrationInfo>();

        /// <summary>
        /// External registration sources.
        /// </summary>
        private readonly List<IRegistrationSource> _dynamicRegistrationSources = new List<IRegistrationSource>();

        /// <summary>
        /// All registrations.
        /// </summary>
        private readonly List<IComponentRegistration> _registrations = new List<IComponentRegistration>();

        private readonly List<IServiceMiddlewareSource> _servicePipelineSources = new List<IServiceMiddlewareSource>();

        /// <summary>
        /// Protects instance variables from concurrent access.
        /// </summary>
        private readonly object _synchRoot = new object();

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultRegisteredServicesTracker"/> class.
        /// </summary>
        public DefaultRegisteredServicesTracker()
        {
            _registrationAccessor = ServiceRegistrationsFor;
        }

        /// <summary>
        /// Fired whenever a component is registered - either explicitly or via a
        /// <see cref="IRegistrationSource"/>.
        /// </summary>
        public event EventHandler<IComponentRegistration>? Registered;

        /// <summary>
        /// Fired when an <see cref="IRegistrationSource"/> is added to the registry.
        /// </summary>
        public event EventHandler<IRegistrationSource>? RegistrationSourceAdded;

        /// <inheritdoc />
        public IEnumerable<IComponentRegistration> Registrations
        {
            get
            {
                lock (_synchRoot)
                    return _registrations.ToList();
            }
        }

        /// <inheritdoc />
        public IEnumerable<IRegistrationSource> Sources
        {
            get
            {
                return _dynamicRegistrationSources.ToList();
            }
        }

        /// <inheritdoc/>
        public IEnumerable<IServiceMiddlewareSource> ServiceMiddlewareSources => _servicePipelineSources;

        /// <inheritdoc/>
        public void AddServiceMiddleware(Service service, IResolveMiddleware middleware, MiddlewareInsertionMode insertionMode = MiddlewareInsertionMode.EndOfPhase)
        {
            var info = GetServiceInfo(service);

            info.UseServiceMiddleware(middleware, insertionMode);
        }

        /// <inheritdoc />
        public virtual void AddRegistration(IComponentRegistration registration, bool preserveDefaults, bool originatedFromDynamicSource = false)
        {
            foreach (var service in registration.Services)
            {
                var info = GetServiceInfo(service);
                info.AddImplementation(registration, preserveDefaults, originatedFromDynamicSource);
            }

            _registrations.Add(registration);
            var handler = Registered;
            handler?.Invoke(this, registration);

            if (originatedFromDynamicSource)
            {
                registration.BuildResolvePipeline(this);
            }
        }

        /// <inheritdoc />
        public void AddRegistrationSource(IRegistrationSource source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            _dynamicRegistrationSources.Insert(0, source);

            var handler = RegistrationSourceAdded;
            handler?.Invoke(this, source);
        }

        /// <inheritdoc/>
        public void AddServiceMiddlewareSource(IServiceMiddlewareSource serviceMiddlewareSource)
        {
            if (serviceMiddlewareSource is null)
            {
                throw new ArgumentNullException(nameof(serviceMiddlewareSource));
            }

            _servicePipelineSources.Add(serviceMiddlewareSource);
        }

        /// <inheritdoc/>
        public IEnumerable<IResolveMiddleware> ServiceMiddlewareFor(Service service)
        {
            lock (_synchRoot)
            {
                var info = GetInitializedServiceInfo(service);
                return info.ServiceMiddleware;
            }
        }

        /// <inheritdoc />
        public bool TryGetRegistration(Service service, [NotNullWhen(returnValue: true)] out IComponentRegistration? registration)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));

            lock (_synchRoot)
            {
                var info = GetInitializedServiceInfo(service);
                return info.TryGetRegistration(out registration);
            }
        }

        /// <inheritdoc/>
        public bool TryGetServiceRegistration(Service service, out ServiceRegistration serviceData)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));

            lock (_synchRoot)
            {
                var info = GetInitializedServiceInfo(service);

                if (info.TryGetRegistration(out var registration))
                {
                    serviceData = new ServiceRegistration(info.ServicePipeline, registration);
                    return true;
                }
            }

            serviceData = default;
            return false;
        }

        /// <inheritdoc />
        public bool IsRegistered(Service service)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));

            lock (_synchRoot)
            {
                return GetInitializedServiceInfo(service).IsRegistered;
            }
        }

        /// <inheritdoc />
        public IEnumerable<IComponentRegistration> RegistrationsFor(Service service)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));

            lock (_synchRoot)
            {
                var info = GetInitializedServiceInfo(service);
                return info.Implementations.ToList();
            }
        }

        /// <inheritdoc/>
        public IEnumerable<ServiceRegistration> ServiceRegistrationsFor(Service service)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));

            lock (_synchRoot)
            {
                var info = GetInitializedServiceInfo(service);

                var listAll = new List<ServiceRegistration>();

                foreach (var implementation in info.Implementations)
                {
                    listAll.Add(new ServiceRegistration(info.ServicePipeline, implementation));
                }

                return listAll;
            }
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected override void Dispose(bool disposing)
        {
            foreach (var registration in _registrations)
                registration.Dispose();

            base.Dispose(disposing);
        }

        private ServiceRegistrationInfo GetInitializedServiceInfo(Service service)
        {
            var info = GetServiceInfo(service);
            if (info.IsInitialized)
                return info;

            if (!info.IsInitializing)
            {
                BeginServiceInfoInitialization(service, info, _dynamicRegistrationSources);
            }

            while (info.HasSourcesToQuery)
            {
                var next = info.DequeueNextSource();
                foreach (var provided in next.RegistrationsFor(service, _registrationAccessor))
                {
                    // This ensures that multiple services provided by the same
                    // component share a single component (we don't re-query for them)
                    foreach (var additionalService in provided.Services)
                    {
                        var additionalInfo = GetServiceInfo(additionalService);
                        if (additionalInfo.IsInitialized || additionalInfo == info) continue;

                        if (!additionalInfo.IsInitializing)
                        {
                            BeginServiceInfoInitialization(additionalService, additionalInfo, ExcludeSource(_dynamicRegistrationSources, next));
                        }
                        else
                        {
                            additionalInfo.SkipSource(next);
                        }
                    }

                    AddRegistration(provided, true, true);
                }
            }

            info.CompleteInitialization();
            return info;
        }

        private void BeginServiceInfoInitialization(Service service, ServiceRegistrationInfo info, IEnumerable<IRegistrationSource> registrationSources)
        {
            info.BeginInitialization(registrationSources);

            // Add any additional service pipeline configuration from external sources.
            foreach (var servicePipelineSource in _servicePipelineSources)
            {
                servicePipelineSource.ProvideMiddleware(service, this, info);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static IEnumerable<IRegistrationSource> ExcludeSource(IEnumerable<IRegistrationSource> sources, IRegistrationSource exclude)
        {
            foreach (var item in sources)
            {
                if (item != exclude)
                {
                    yield return item;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ServiceRegistrationInfo GetServiceInfo(Service service)
        {
            if (_serviceInfo.TryGetValue(service, out var existing))
                return existing;

            var info = new ServiceRegistrationInfo(service);
            _serviceInfo.Add(service, info);
            return info;
        }
    }
}
