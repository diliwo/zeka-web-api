using DiliBeneficiary.Core.Common;

namespace DiliBeneficiary.Core.Entities
{
    public class ContratPiis : Entity
    {
        public int IdSource { get; set; }
        public string Libelle { get; set; }
        public string CommentaireStatut { get; set; }
        public string AsTraitant { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public DateTime? DateOfSigning { get; set; }
    }
}
