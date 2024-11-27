using Client.Application.Assessments.Common;
using Client.Application.Common.Exceptions;
using Client.Core.Entities;
using Client.Core.Enums;
using Client.Core.Interfaces;
using MediatR;

namespace Client.Application.Assessments.Commands.CreateAssessment
{
    public class CreateAssessmentCommand : IRequest<int>
    {
        public bool IsFinalized { get; set; }
        public int ClientId { get; set; }
        public string PersonalSituationFamily { get; set; }
        public string PersonalSituationHousing { get; set; }
        public string PersonalSituationHealth { get; set; }
        public string PersonalSituationFinancialSituation { get; set; }
        public string PersonalSituationAdministrativeStatus { get; set; }
        public string LanguageTrainingNote { get; set; }
        public string TrainingDifficulty { get; set; }
        public string TrainingOpinion { get; set; }
        public string TrainingFacilitiesAndStrengths { get; set; }
        public string TrainingPersonalImprovments { get; set; }
        public string TrainingConsultantNote { get; set; }
        public string TrainingConsultantLanguageLearningNote { get; set; }
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
        public List<ProfessionalAssessmentDto> BilanProfessions { get; set; }

        public class CreateAssessmentCommandHandler : IRequestHandler<CreateAssessmentCommand, int>
        {
            private readonly IRepositoryManager _repository;
            //private readonly IHttpContextAccessor _httpContextAccessor;

            public CreateAssessmentCommandHandler(
                IRepositoryManager repository
            )
            {
                _repository = repository;
            }

            public async Task<int> Handle(CreateAssessmentCommand request, CancellationToken cancellationToken)
            {
                var Client = _repository.Client.Get(request.ClientId, true);

                if (Client == null)
                {
                    throw new NotFoundException(nameof(Client), request.ClientId);
                }

                Assessment assessment = new Assessment(
                    request.IsFinalized,
                    Client,
                   "System",
                    request.PersonalSituationFamily?.Trim(),
                    request.PersonalSituationHousing?.Trim(),
                    request.PersonalSituationHealth?.Trim(),
                    request.PersonalSituationFinancialSituation?.Trim(),
                    request.PersonalSituationAdministrativeStatus?.Trim(),
                    request.LanguageTrainingNote?.Trim(),
                    request.TrainingDifficulty?.Trim(),
                    request.TrainingOpinion?.Trim(),
                    request.TrainingFacilitiesAndStrengths?.Trim(),
                    request.TrainingPersonalImprovments?.Trim(),
                    request.TrainingConsultantNote?.Trim(),
                    request.TrainingConsultantLanguageLearningNote?.Trim(),
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
                        if (professionBilan != null && assessment.BilanProfessions != null)
                        {
                            var idBilanProfession = (professionBilan.BilanProfessionId != null)
                                ? (int)professionBilan.BilanProfessionId
                                : -1;

                            var bilanProfession = _repository.ProfessionalAssessment.Get(idBilanProfession);

                            if (bilanProfession != null)
                            {
                                int indexOf = assessment.BilanProfessions.IndexOf(bilanProfession);

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
                                assessment.BilanProfessions.Add(bilanProfession);
                            }
                            else
                            {
                                if (professionBilan.ProfessionId != null)
                                {
                                    var profession = _repository.Profession.Get((int)professionBilan.ProfessionId);

                                    if (profession != null)
                                    {
                                        assessment.CreateBilanProfession(profession,
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

                _repository.Assessment.Persist(assessment);

                return assessment.Id;
            }
        }

    }
}