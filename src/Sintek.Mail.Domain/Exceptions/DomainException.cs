namespace Sintek.Mail.Domain.Exceptions;

/// <summary>
/// Base exception for all domain-related errors.
/// </summary>
/// <remarks>
/// Deriva de <see cref="ArgumentException"/> por decisao D-007: toda violacao
/// deste dominio nasce de um argumento invalido vindo da borda (endereco mal
/// formado, dominio que nao casa, pasta ja restrita), e alinhar com o tipo do
/// BCL deixa o contrato legivel para quem chama sem conhecer estas classes.
///
/// Note que <c>Assert.Throws&lt;ArgumentException&gt;</c> do xunit exige tipo
/// EXATO e continua falhando com as derivadas; os testes usam
/// <c>Assert.ThrowsAny&lt;ArgumentException&gt;</c>.
/// </remarks>
public abstract class DomainException : ArgumentException
{
    protected DomainException(string message) : base(message) { }
    protected DomainException(string message, Exception innerException) : base(message, innerException) { }
}
