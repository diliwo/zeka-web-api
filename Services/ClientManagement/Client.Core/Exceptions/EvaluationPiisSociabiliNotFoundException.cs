namespace Client.Core.Exceptions
{
    public class EvaluationPiisSociabiliNotFoundException : Exception
    {
        public EvaluationPiisSociabiliNotFoundException(string typeInfo, string data)
            :base($"Aucune évaluation trouvée dans Sociabili avec le \"{typeInfo}\" \"{data}\"!")
        {
        }
    }
}
