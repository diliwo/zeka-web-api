using DiliBeneficiary.Application.Bilans.Common;
using DiliBeneficiary.Application.Common.Exceptions;
using DiliBeneficiary.Core.Entities;
using DiliBeneficiary.Core.Enums;
using DiliBeneficiary.Core.Interfaces;
using MediatR;

namespace DiliBeneficiary.Application.Bilans.Commands.CreateBilan
{
    public class CreateBilanCommand : IRequest<int>
    {
        public bool IsFinalized { get; set; }
        public int BeneficiaryId { get; set; }
        public string PersonalSituationFamily { get; set; }
        public string PersonalSituationHousing { get; set; }
        public string PersonalSituationHealth { get; set; }
        public string PersonalSituationFinancialSituation { get; set; }
        public string PersonalSituationAdministrativeStatus { get; set; }
        public string LanguageFormationNote { get; set; }
        public string FormationDifficulty { get; set; }
        public string FormationOpinion { get; set; }
        public string FormationFacilitiesAndStrengths { get; set; }
        public string FormationPersonalImprovments { get; set; }
        public string FormationConsultantNote { get; set; }
        public string FormationConsultantLanguageLearningNote { get; set; }
        public string ProfessionalExperienceProblemEncountered { get; set; }
        public string ProfessionalExperienceWhatsRewarding { get; set; }
        public string ProfessionalExperienceKnowledge { get; set; }
        public string ProfessionalExperiencePointToImprove { get; set; }
        public string ProfessionalExperienceNote { get; set; }
        public string ProfessionalExpectationWorkingConditionWhatIWant { get; set; }
        public string ProfessionalExpectationWorkingConditionWhatIDontWant { get; set; }
        public string ProfessionalExpectationWorkingConditionWhatMotivatesMe { get; set; }
        public string ProfessionalExpectationWorkingConditionConsultantNote { get; set; }
        public string ProfessionalExpectationShortTermA { get; set; }
        public string ProfessionalExpectationShortTermB { get; set; }
        public string ProfessionalExpectationMediumTerm { get; set; }
        public string ProfessionalExpectationLongTerm { get; set; }
        public PersonalExpectationLanguageknowledgeScore ProfessionalExpectationNlOralLanguageScore { get; set; }
        public PersonalExpectationLanguageknowledgeScore ProfessionalExpectationNlWrittentLanguageScore { get; set; }
        public PersonalExpectationLanguageknowledgeScore ProfessionalExpectationFrOralLanguageScore { get; set; }
        public PersonalExpectationLanguageknowledgeScore ProfessionalExpectationFrWrittenLanguageScore { get; set; }
        public bool ProfessionalExpectationItKnowledgeEmail { get; set; }
        public bool ProfessionalExpectationItKnowledgeInternet { get; set; }
        public bool ProfessionalExpectationItKnowledgeWord { get; set; }
        public List<BilanProfessionDto> BilanProfessions { get; set; }

        public class CreateBilanCommandHandler : IRequestHandler<CreateBilanCommand, int>
        {
            private readonly IRepositoryManager _repository;
            //private readonly IHttpContextAccessor _httpContextAccessor;

            public CreateBilanCommandHandler(
                IRepositoryManager repository
               // IHttpContextAccessor httpContextAccessor
            )
            {
                _repository = repository;
               // _httpContextAccessor = httpContextAccessor;
            }

            public async Task<int> Handle(CreateBilanCommand request, CancellationToken cancellationToken)
            {
                var beneficiary = _repository.Beneficiary.Get(request.BeneficiaryId, true);

                if (beneficiary == null)
                {
                    throw new NotFoundException(nameof(Beneficiary), request.BeneficiaryId);
                }

                Bilan bilan = new Bilan(
                    request.IsFinalized,
                    beneficiary,
                   "System",
                    request.PersonalSituationFamily?.Trim(),
                    request.PersonalSituationHousing?.Trim(),
                    request.PersonalSituationHealth?.Trim(),
                    request.PersonalSituationFinancialSituation?.Trim(),
                    request.PersonalSituationAdministrativeStatus?.Trim(),
                    request.LanguageFormationNote?.Trim(),
                    request.FormationDifficulty?.Trim(),
                    request.FormationOpinion?.Trim(),
                    request.FormationFacilitiesAndStrengths?.Trim(),
                    request.FormationPersonalImprovments?.Trim(),
                    request.FormationConsultantNote?.Trim(),
                    request.FormationConsultantLanguageLearningNote?.Trim(),
                    request.ProfessionalExperienceProblemEncountered?.Trim(),
                    request.ProfessionalExperienceWhatsRewarding?.Trim(),
                    request.ProfessionalExperienceKnowledge?.Trim(),
                    request.ProfessionalExperiencePointToImprove?.Trim(),
                    request.ProfessionalExperienceNote?.Trim(),
                    request.ProfessionalExpectationWorkingConditionWhatIWant?.Trim(),
                    request.ProfessionalExpectationWorkingConditionWhatIDontWant?.Trim(),
                    request.ProfessionalExpectationWorkingConditionWhatMotivatesMe?.Trim(),
                    request.ProfessionalExpectationWorkingConditionConsultantNote?.Trim(),
                    request.ProfessionalExpectationShortTermA?.Trim(),
                    request.ProfessionalExpectationShortTermB?.Trim(),
                    request.ProfessionalExpectationMediumTerm?.Trim(),
                    request.ProfessionalExpectationLongTerm?.Trim(),
                    request.ProfessionalExpectationNlOralLanguageScore,
                    request.ProfessionalExpectationNlWrittentLanguageScore,
                    request.ProfessionalExpectationFrOralLanguageScore,
                    request.ProfessionalExpectationFrWrittenLanguageScore,
                    request.ProfessionalExpectationItKnowledgeEmail,
                    request.ProfessionalExpectationItKnowledgeInternet,
                    request.ProfessionalExpectationItKnowledgeWord
                );


                if (request.BilanProfessions != null)
                {
                    foreach (var professionBilan in request.BilanProfessions)
                    {
                        if (professionBilan != null && bilan.BilanProfessions != null)
                        {
                            var idBilanProfession = (professionBilan.BilanProfessionId != null)
                                ? (int)professionBilan.BilanProfessionId
                                : -1;

                            var bilanProfession = _repository.ProfessionBilan.Get(idBilanProfession);

                            if (bilanProfession != null)
                            {
                                int indexOf = bilan.BilanProfessions.IndexOf(bilanProfession);

                                if (indexOf > -1)
                                {
                                    //Update the parameters
                                    bilanProfession.ProfessionId = professionBilan.ProfessionId;
                                    bilanProfession.AcquiredBehaviouralKnowledge = professionBilan.AcquiredBehaviouralKnowledge?.Trim();
                                    bilanProfession.AcquiredKnowledge = professionBilan.AcquiredKnowledge?.Trim();
                                    bilanProfession.AcquiredKnowHow = professionBilan.AcquiredKnowHow?.Trim();
                                    bilanProfession.BehaviouralKnowledgeToDevelop = professionBilan.BehaviouralKnowledgeToDevelop?.Trim();
                                    bilanProfession.KnowHowToDevelop = professionBilan.KnowHowToDevelop?.Trim();
                                    bilanProfession.KnowledgeToDevelop = professionBilan.KnowledgeToDevelop?.Trim();
                                }
                                bilan.BilanProfessions.Add(bilanProfession);
                            }
                            else
                            {
                                if (professionBilan.ProfessionId != null)
                                {
                                    var profession = _repository.Profession.Get((int)professionBilan.ProfessionId);

                                    if (profession != null)
                                    {
                                        bilan.CreateBilanProfession(profession,
                                            professionBilan.AcquiredKnowledge?.Trim(),
                                            professionBilan.AcquiredBehaviouralKnowledge?.Trim(),
                                            professionBilan.AcquiredKnowHow?.Trim(),
                                            professionBilan.KnowledgeToDevelop?.Trim(),
                                            professionBilan.BehaviouralKnowledgeToDevelop?.Trim(),
                                            professionBilan.KnowHowToDevelop);
                                    }
                                    else
                                    {
                                        throw new NotFoundException(nameof(profession));
                                    }

                                }
                            }
                        }
                    }
                }

                _repository.Bilan.Persist(bilan);

                return bilan.Id;
            }
        }

    }
}