// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

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

        /// <summary>Plan contains a field that we cannot safely ignore during translation.</summary>
        internal static SubstraitTranslationException UnexpectedField(string messageName, int fieldNumber)
            => new($"Unexpected field {fieldNumber} in Substrait {messageName}. " +
                   $"This field may affect query semantics and cannot be safely ignored.");

        /// <summary>Unsupported literal type.</summary>
        internal static SubstraitTranslationException UnsupportedLiteral(int fieldNumber)
            => new($"Unsupported Substrait literal type (field={fieldNumber}). " +
                   $"Supported: boolean(1), i8(2), i16(3), i32(5), i64(7), fp32(10), fp64(11), string(12), null(26).");
    }
}
