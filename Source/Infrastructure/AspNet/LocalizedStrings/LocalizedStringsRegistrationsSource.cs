/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2017 International Federation of Red Cross. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using Autofac.Core;
using System.Linq;
using Autofac.Core.Registration;
using Autofac.Core.Activators.Delegate;
using Autofac.Core.Lifetime;

namespace Infrastructure.AspNet.LocalizedStrings
{
    internal class LocalizedStringsRegistrationsSource : IRegistrationSource
    {
        private readonly ILocalizedStringsParser _parser;

        public LocalizedStringsRegistrationsSource(ILocalizedStringsParser parser)
        {
            _parser = parser;
        }

        public bool IsAdapterForIndividualComponents => false;

        public IEnumerable<IComponentRegistration> RegistrationsFor(Service service, Func<Service, IEnumerable<IComponentRegistration>> registrationAccessor)
        {
            if (!(service is IServiceWithType serviceWithType) || serviceWithType.ServiceType != typeof(LocalizedStringsProvider))
                return Enumerable.Empty<IComponentRegistration>();

            var stringProviders = _parser.GetAllProviders();

            var registrations = stringProviders.Select(provider =>
                new ComponentRegistration(
                    Guid.NewGuid(),
                    new DelegateActivator(
                        serviceWithType.ServiceType, (c, p) => provider),
                    new CurrentScopeLifetime(),
                        InstanceSharing.Shared,
                        InstanceOwnership.OwnedByLifetimeScope,
                        new[] { service },
                        new Dictionary<string, object>()));

            return registrations;
        }
    }
}
