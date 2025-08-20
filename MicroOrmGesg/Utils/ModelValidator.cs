using System.Reflection;

namespace MicroOrmGesg.Utils;

public sealed class ModelValidationError
{
    public required string PropertyName { get; init; }
    public required string Message { get; init; }
}

public static class ModelValidator
{
    /// <summary>
    /// Valida un modelo utilizando los atributos [Required] y [StringLength].
    /// </summary>
    /// <param name="model">Instancia a validar.</param>
    /// <returns>Listado de errores encontrados. Si está vacío, la validación fue exitosa.</returns>
    public static List<ModelValidationError> Validate(object model)
    {
        if (model is null) throw new ArgumentNullException(nameof(model));

        var errors = new List<ModelValidationError>();
        var type = model.GetType();
        var props = type.GetProperties(BindingFlags.Instance | BindingFlags.Public);

        foreach (var prop in props)
        {
            // Ignorar si la propiedad no es legible
            if (!prop.CanRead) continue;

            var value = prop.GetValue(model);
            var propName = prop.Name;

            // Required
            var reqAttr = prop.GetCustomAttribute<RequiredAttribute>();
            if (reqAttr is not null)
            {
                var isOk = value is not null;
                if (isOk && value is string s)
                {
                    // No se permite vacío o solo espacios
                    isOk = !string.IsNullOrWhiteSpace(s);
                }

                if (!isOk)
                {
                    errors.Add(new ModelValidationError
                    {
                        PropertyName = propName,
                        Message = reqAttr.Message ?? $"El campo '{propName}' es obligatorio."
                    });
                    // Si ya está vacío y es obligatorio, no tiene sentido aplicar StringLength
                    continue;
                }
            }

            // StringLength
            var lenAttr = prop.GetCustomAttribute<StringLengthAttribute>();
            if (lenAttr is not null)
            {
                if (value is string s)
                {
                    var length = s?.Length ?? 0;

                    if (lenAttr.Min > 0 && length < lenAttr.Min)
                    {
                        errors.Add(new ModelValidationError
                        {
                            PropertyName = propName,
                            Message = lenAttr.Message ?? $"El campo '{propName}' debe tener al menos {lenAttr.Min} caracteres."
                        });
                    }

                    if (lenAttr.Max > 0 && length > lenAttr.Max)
                    {
                        errors.Add(new ModelValidationError
                        {
                            PropertyName = propName,
                            Message = lenAttr.Message ?? $"El campo '{propName}' no puede superar los {lenAttr.Max} caracteres."
                        });
                    }
                }
                else
                {
                    // No aplica StringLength a tipos no string; no se genera error.
                }
            }
        }

        return errors;
    }
}
