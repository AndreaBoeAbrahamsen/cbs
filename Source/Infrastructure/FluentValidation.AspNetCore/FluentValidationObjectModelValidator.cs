/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2017 International Federation of Red Cross. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Internal;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace FluentValidation.AspNetCore
{
    public class FluentValidationObjectModelValidator : IObjectModelValidator
    {
        public const string InvalidValuePlaceholder = "__FV_InvalidValue";
        public const string ModelKeyPrefix = "__FV_Prefix_";

        private IModelMetadataProvider _modelMetadataProvider;
        private readonly ValidatorCache _validatorCache;
        private CompositeModelValidatorProvider _validatorProvider;

        /// <summary>
        ///     Initializes a new instance of <see cref="FluentValidationObjectModelValidator" />.
        /// </summary>
        public FluentValidationObjectModelValidator(IModelMetadataProvider modelMetadataProvider,
            IList<IModelValidatorProvider> validatorProviders)
        {
            _modelMetadataProvider =
                modelMetadataProvider ?? throw new ArgumentNullException(nameof(modelMetadataProvider));
            _validatorCache = new ValidatorCache();
            _validatorProvider = new CompositeModelValidatorProvider(validatorProviders);
        }

        /// <summary>
        /// Whether or not to run the default MVC validation pipeline after FluentValidation has executed. Default is true. 
        /// </summary>
        public bool RunDefaultMvcValidation { get; set; } = true;

        /// <summary>
        /// Whether or not child properties should be implicitly validated if a matching validator can be found. By default this is false, and you should wire up child validators using SetValidator.
        /// </summary>
        private bool ImplicitlyValidateChildProperties { get; set; }

        public void Validate(ActionContext actionContext, ValidationStateDictionary validationState, string prefix,
            object model)
        {
            if (actionContext == null)
            {
                throw new ArgumentNullException(nameof(actionContext));
            }

            var validatorFactory =
                (IValidatorFactory) actionContext.HttpContext.RequestServices.GetService(typeof(IValidatorFactory));

            IValidator validator = null;
            var metadata = model == null ? null : _modelMetadataProvider.GetMetadataForType(model.GetType());

            var prependPrefix = true;

            if (model != null)
            {
                validator = validatorFactory.GetValidator(metadata.ModelType);

                if (validator == null && metadata.IsCollectionType)
                {
                    validator = BuildCollectionValidator(prefix, metadata, validatorFactory);
                    prependPrefix = false;
                }
            }

            if (validator == null)
            {
                // Use default impl if FV doesn't have a validator for the type.
                RunDefaultValidation(actionContext, _validatorProvider, validationState, prefix, model, metadata,
                    ImplicitlyValidateChildProperties, validatorFactory, new CustomizeValidatorAttribute());

                return;
            }

            var customizations = GetCustomizations(actionContext, model.GetType(), prefix);

            var selector = customizations.ToValidatorSelector();
            var interceptor = customizations.GetInterceptor() ?? validator as IValidatorInterceptor;
            var context =
                new FluentValidation.ValidationContext(model, new FluentValidation.Internal.PropertyChain(), selector);

            if (interceptor != null)
            {
                // Allow the user to provide a customized context
                // However, if they return null then just use the original context.
                context = interceptor.BeforeMvcValidation((ControllerContext) actionContext, context) ?? context;
            }

            var result = validator.Validate(context);

            if (interceptor != null)
            {
                // allow the user to provice a custom collection of failures, which could be empty.
                // However, if they return null then use the original collection of failures. 
                result = interceptor.AfterMvcValidation((ControllerContext) actionContext, context, result) ?? result;
            }

            if (!string.IsNullOrEmpty(prefix))
            {
                prefix = prefix + ".";
            }

            var keysProcessed = new HashSet<string>();

            //First pass: Clear out any model errors for these properties generated by MVC.
            foreach (var modelError in result.Errors)
            {
                var key = modelError.PropertyName;

                if (prependPrefix)
                {
                    key = prefix + key;
                }
                else
                {
                    key = key.Replace(ModelKeyPrefix, string.Empty);
                }

                // See if there's already an item in the ModelState for this key. 
                if (actionContext.ModelState.ContainsKey(key) && !keysProcessed.Contains(key))
                {
                    actionContext.ModelState[key].Errors.Clear();
                }

                keysProcessed.Add(key);
                actionContext.ModelState.AddModelError(key, modelError.ErrorMessage);
            }

            // Now allow the default MVC validation to run.  
            if (RunDefaultMvcValidation)
            {
                RunDefaultValidation(actionContext, _validatorProvider, validationState, prefix, model, metadata,
                    ImplicitlyValidateChildProperties, validatorFactory, customizations);
            }

            HandleIValidatableObject(actionContext, prefix, model, prependPrefix);
        }

        private static void HandleIValidatableObject(ActionContext actionContext, string prefix, object model,
            bool prependPrefix)
        {
            var validatable = model as IValidatableObject;

            if (validatable != null)
            {
                var validationContext = new System.ComponentModel.DataAnnotations.ValidationContext(
                    instance: validatable,
                    serviceProvider: actionContext.HttpContext?.RequestServices,
                    items: null);

                foreach (var ivalresult in validatable.Validate(validationContext)
                    .Where(x => x != ValidationResult.Success))
                {
                    foreach (var memberName in ivalresult.MemberNames)
                    {
                        var key = memberName;

                        if (prependPrefix)
                        {
                            key = prefix + key;
                        }
                        else
                        {
                            key = key.Replace(ModelKeyPrefix, string.Empty);
                        }

                        actionContext.ModelState.AddModelError(key, ivalresult.ErrorMessage);
                    }
                }
            }
        }

/*
		private IModelValidatorProvider GetModelValidatorProvider(IValidatorFactory validatorFactory, CustomizeValidatorAttribute customizations) {
			var modelValidatorProvider = ImplicitlyValidateChildProperties ? new FluentValidationModelValidatorProvider(validatorFactory, customizations, _validatorProvider) : (IModelValidatorProvider) _validatorProvider;
			return modelValidatorProvider;
		}
*/


        protected virtual void RunDefaultValidation(ActionContext actionContext,
            IModelValidatorProvider validatorProvider, ValidationStateDictionary validationState, string prefix,
            object model, ModelMetadata metadata, bool implicitlyValidateChildPropertiesUsingFluentValidation,
            IValidatorFactory factory, CustomizeValidatorAttribute customizations)
        {
            ValidationVisitor visitor;

//			if (ImplicitlyValidateChildProperties) {
//				visitor = new CustomValidationVisitor(actionContext, validatorProvider, _validatorCache, _modelMetadataProvider, validationState, factory, customizations);
//			}
//			else {
            visitor = new ValidationVisitor(
                actionContext,
                validatorProvider,
                _validatorCache,
                _modelMetadataProvider,
                validationState);
//			}
            visitor.Validate(metadata, prefix, model);
        }

        private CustomizeValidatorAttribute GetCustomizations(ActionContext actionContext, Type type, string prefix)
        {
            if (actionContext?.ActionDescriptor?.Parameters == null)
            {
                return new CustomizeValidatorAttribute();
            }

            var descriptors = actionContext.ActionDescriptor.Parameters
                .Where(x => x.ParameterType == type)
                .Where(x =>
                    x.BindingInfo != null && x.BindingInfo.BinderModelName != null &&
                    x.BindingInfo.BinderModelName == prefix || x.Name == prefix ||
                    prefix == string.Empty && x.BindingInfo?.BinderModelName == null)
                .OfType<ControllerParameterDescriptor>()
                .ToList();

            CustomizeValidatorAttribute attribute = null;

            if (descriptors.Count == 1)
            {
                attribute = descriptors[0].ParameterInfo.GetCustomAttributes(typeof(CustomizeValidatorAttribute), true)
                    .FirstOrDefault() as CustomizeValidatorAttribute;
            }

            if (descriptors.Count > 1)
            {
                // We found more than 1 matching with same prefix and name. 
            }

            return attribute ?? new CustomizeValidatorAttribute();
        }

        private IValidator BuildCollectionValidator(string prefix, ModelMetadata collectionMetadata,
            IValidatorFactory validatorFactory)
        {
            var elementValidator = validatorFactory.GetValidator(collectionMetadata.ElementType);
            if (elementValidator == null) return null;

            var type = typeof(MvcCollectionValidator<>).MakeGenericType(collectionMetadata.ElementType);
            var validator = (IValidator) Activator.CreateInstance(type, elementValidator, prefix);
            return validator;
        }
    }

    internal class MvcCollectionValidator<T> : AbstractValidator<IEnumerable<T>>
    {
        public MvcCollectionValidator(IValidator<T> validator, string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) prefix = FluentValidationObjectModelValidator.ModelKeyPrefix;
            RuleFor(x => x).SetCollectionValidator(validator).OverridePropertyName(prefix);
        }
    }

    internal class CustomValidationVisitor : ValidationVisitor
    {
        private ModelStateDictionary _modelState;
        private ValidatorCache _validatorCache;
        private readonly IModelMetadataProvider _metadataProvider;
        private ActionContext _actionContext;
        private IModelValidatorProvider _validatorProvider;

        private IValidatorFactory _validatorFactory;
        private CustomizeValidatorAttribute _cutomizations;

        private static Func<ValidationVisitor, string> _keyGetter;
        private static Func<ValidationVisitor, ModelMetadata> _metadataGetter;
        private static Func<ValidationVisitor, object> _modelGetter;
        private static Func<ValidationVisitor, object> _containerGetter;


        static CustomValidationVisitor()
        {
            _keyGetter = CreateGetFieldDelegate<ValidationVisitor, string>("_key");
            _metadataGetter = CreateGetFieldDelegate<ValidationVisitor, ModelMetadata>("_metadata");
            _modelGetter = CreateGetFieldDelegate<ValidationVisitor, object>("_model");
            _containerGetter = CreateGetFieldDelegate<ValidationVisitor, object>("_container");
        }

        static public Func<S, T> CreateGetFieldDelegate<S, T>(string fieldName)
        {
            var type = typeof(S);
            var fld = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);

            var instExp = Expression.Parameter(type);
            var fieldExp = Expression.Field(instExp, fld);
            return Expression.Lambda<Func<S, T>>(fieldExp, instExp).Compile();
        }

        public CustomValidationVisitor(ActionContext actionContext, IModelValidatorProvider validatorProvider,
            ValidatorCache validatorCache, IModelMetadataProvider metadataProvider,
            ValidationStateDictionary validationState, IValidatorFactory validatorFactory,
            CustomizeValidatorAttribute cutomizations) : base(actionContext, validatorProvider, validatorCache,
            metadataProvider, validationState)
        {
            _modelState = actionContext.ModelState;
            _validatorCache = validatorCache;
            _metadataProvider = metadataProvider;
            _validatorFactory = validatorFactory;
            _cutomizations = cutomizations;
            _actionContext = actionContext;
            _validatorProvider = validatorProvider;
        }


        protected override bool ValidateNode()
        {
            // private field workaround...
            var _key = _keyGetter(this);
            var _metadata = _metadataGetter(this);
            var _container = _containerGetter(this);
            var _model = _modelGetter(this);

            // This method is dupliated from MVC's VlaidationVisitor.cs
            // They only validate if validationstate != invalid, but if there's a FV validator, we want to override this behaviour. 

            var fluentValidator = _validatorFactory.GetValidator(_metadata.ModelType);

            var state = _modelState.GetValidationState(_key);
            // Rationale: we might see the same model state key used for two different objects.
            // We want to run validation unless it's already known that this key is invalid.
            if (state != ModelValidationState.Invalid || fluentValidator != null)
            {
                var validators = _validatorCache.GetValidators(_metadata, _validatorProvider).ToList();

                if (fluentValidator != null)
                {
                    validators.Add(new FluentValidationModelValidator(fluentValidator, _cutomizations));
                }

                var count = validators.Count;
                if (count > 0)
                {
                    var context = new ModelValidationContext(
                        _actionContext,
                        _metadata,
                        _metadataProvider,
                        _container,
                        _model);

                    var results = new List<ModelValidationResult>();
                    for (var i = 0; i < count; i++)
                    {
                        results.AddRange(validators[i].Validate(context));
                    }

                    var resultsCount = results.Count;
                    for (var i = 0; i < resultsCount; i++)
                    {
                        var result = results[i];
                        var key = ModelNames.CreatePropertyModelName(_key, result.MemberName);
                        _modelState.TryAddModelError(key, result.Message);
                    }
                }
            }

            state = _modelState.GetFieldValidationState(_key);

            if (state == ModelValidationState.Invalid)
            {
                return false;
            }
            else
            {
                // If the field has an entry in ModelState, then record it as valid. Don't create
                // extra entries if they don't exist already.
                var entry = _modelState[_key];
                if (entry != null)
                {
                    entry.ValidationState = ModelValidationState.Valid;
                }

                return true;
            }
        }
    }
}