// Copyright (c) Microsoft Corporation.  All rights reserved.

using Apache.Arrow.Adbc;

namespace KustoAdbc.Substrait
{
    /// <summary>
    /// Exception thrown when a Substrait plan cannot be translated to KQL.
    /// Provides a structured error message identifying the specific cause.
    /// </summary>
    public class SubstraitTranslationException : AdbcException
    {
        public SubstraitTranslationException(string message)
            : base(message, AdbcStatusCode.InvalidArgument)
        {
        }

        public SubstraitTranslationException(string message, System.Exception innerException)
            : base(message, innerException)
        {
        }

        /// <summary>Malformed plan structure (missing required fields).</summary>
        internal static SubstraitTranslationException MalformedPlan(string detail)
            => new($"Malformed Substrait plan: {detail}");

        /// <summary>Plan references a function not declared in extensions.</summary>
        internal static SubstraitTranslationException UndeclaredFunction(int functionRef)
            => new($"Substrait plan references undeclared function (anchor={functionRef}). " +
                   $"Ensure the plan includes a SimpleExtensionDeclaration for this function.");

        /// <summary>Plan declares a function that has no KQL equivalent.</summary>
        internal static SubstraitTranslationException UnsupportedFunction(string functionSignature)
            => new($"Unsupported Substrait function '{functionSignature}' has no KQL equivalent. " +
                   $"Use KustoCapabilities.GetCapabilityYaml() to discover supported functions.");

        /// <summary>Plan uses a relation type we don't support.</summary>
        internal static SubstraitTranslationException UnsupportedRelation(int relFieldNumber)
            => new($"Unsupported Substrait relation type (field={relFieldNumber}). " +
                   $"Supported: read(1), filter(2), fetch(3), aggregate(4), sort(5), join(6), project(7).");

        /// <summary>Expression type not supported.</summary>
        internal static SubstraitTranslationException UnsupportedExpression(string detail)
            => new($"Unsupported Substrait expression: {detail}");
    }
}
