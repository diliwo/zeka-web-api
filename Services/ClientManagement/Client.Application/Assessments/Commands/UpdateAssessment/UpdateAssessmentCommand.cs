using Client.Application.Assessments.Common;
using Client.Application.Common.Exceptions;
using Client.Core.Entities;
using Client.Core.Enums;
using Client.Core.Interfaces;
using MediatR;

namespace Client.Application.Assessments.Commands.UpdateAssessment
{
    public class UpdateAssessmentCommand : IRequest<int>
    {
        public int? BilanId { get; set; }
        public bool IsFinalized { get; set; }
        public int ClientId { get; set; }
        public string UserName { get; set; }
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

        public class UpdateBilanCommandHandler : IRequestHandler<UpdateAssessmentCommand, int>
        {
            private readonly IRepositoryManager _repository;

            public UpdateBilanCommandHandler(
                IRepositoryManager repository
                )
            {
                _repository = repository;
            }

            public async Task<int> Handle(UpdateAssessmentCommand request, CancellationToken cancellationToken)
            {
                var bilan = _repository.Assessment.GetBilanById((int)request.BilanId);

                if (bilan == null)
                {
                    throw new NotFoundException(nameof(Assessment), request.BilanId);
                }

                bilan.IsFinalized = request.IsFinalized;
                bilan.UserName = request.UserName;
                bilan.PersonalSituationFamily = request.PersonalSituationFamily?.Trim();
                bilan.PersonalSituationHousing =  request.PersonalSituationHousing?.Trim();
                bilan.PersonalSituationHealth =  request.PersonalSituationHealth?.Trim();
                bilan.PersonalSituationFinancialSituation = request.PersonalSituationFinancialSituation?.Trim();
                bilan.PersonalSituationAdministrativeStatus = request.PersonalSituationAdministrativeStatus?.Trim();
                bilan.TrainingConsultantNote = request.LanguageTrainingNote?.Trim();
                bilan.TrainingDifficulty = request.TrainingDifficulty?.Trim();
                bilan.TrainingOpinion = request.TrainingOpinion?.Trim();
                bilan.TrainingFacilitiesAndStrengths = request.TrainingFacilitiesAndStrengths?.Trim();
                bilan.TrainingPersonalImprovments = request.TrainingPersonalImprovments?.Trim();
                bilan.TrainingConsultantNote = request.TrainingConsultantNote?.Trim();
                bilan.TrainingConsultantLanguageLearningNote = request.TrainingConsultantLanguageLearningNote?.Trim();
                bilan.ProfessionalExperienceProblemEncountered = request.ProfessionalExperienceProblemEncountered?.Trim();
                bilan.ProfessionalExperienceWhatsRewarding = request.ProfessionalExperienceWhatsRewarding?.Trim();
                bilan.ProfessionalExperienceNote = request.ProfessionalExperienceNote?.Trim();
                bilan.ProfessionalExperiencePointToImprove = request.ProfessionalExperiencePointToImprove?.Trim();
                bilan.ProfessionalExperienceKnowledge = request.ProfessionalExperienceKnowledge?.Trim();
                bilan.ProfessionalExpectationWorkingConditionWhatIWant = request.ProfessionalExpectationWorkingConditionWhatIWant?.Trim();
                bilan.ProfessionalExpectationWorkingConditionWhatIDontWant = request.ProfessionalExpectationWorkingConditionWhatIDontWant?.Trim();
                bilan.ProfessionalExpectationWorkingConditionWhatMotivatesMe =
                    request.ProfessionalExpectationWorkingConditionWhatMotivatesMe?.Trim();
                bilan.ProfessionalExpectationWorkingConditionConsultantNote = request.ProfessionalExpectationWorkingConditionConsultantNote?.Trim();
                bilan.ProfessionalExpectationShortTermA = request.ProfessionalExpectationShortTermA?.Trim();
                bilan.ProfessionalExpectationShortTermB = request.ProfessionalExpectationShortTermB?.Trim();
                bilan.ProfessionalExpectationMediumTerm = request.ProfessionalExpectationMediumTerm?.Trim();
                bilan.ProfessionalExpectationLongTerm = request.ProfessionalExpectationLongTerm?.Trim();
                bilan.ProfessionalExpectationFrWrittenLanguageScore = request.ProfessionalExpectationFrWrittenLanguageScore;
                bilan.ProfessionalExpectationNlWrittentLanguageScore = request.ProfessionalExpectationNlWrittentLanguageScore;
                bilan.ProfessionalExpectationFrOralLanguageScore = request.ProfessionalExpectationFrOralLanguageScore;
                bilan.ProfessionalExpectationNlOralLanguageScore = request.ProfessionalExpectationNlOralLanguageScore;
                bilan.ProfessionalExpectationItKnowledgeEmail = request.ProfessionalExpectationItKnowledgeEmail;
                bilan.ProfessionalExpectationItKnowledgeInternet = request.ProfessionalExpectationItKnowledgeInternet;
                bilan.ProfessionalExpectationItKnowledgeWord = request.ProfessionalExpectationItKnowledgeWord;

                if (request.BilanProfessions != null)
                {
                    foreach (var professionBilan in request.BilanProfessions)
                    {
                        if (professionBilan != null && bilan.BilanProfessions != null)
                        {
                            var idBilanProfession = (professionBilan.BilanProfessionId != null)
                                ? (int)professionBilan.BilanProfessionId
                                : -1;

                            var bilanProfession = _repository.ProfessionalAssessment.Get(idBilanProfession);

                            if (bilanProfession != null)
                            {
                                int indexOf = bilan.BilanProfessions.ToList().FindIndex(b => b.Id == bilanProfession.Id);

                                if (indexOf > -1)
                                {
                                    bilanProfession.ProfessionId = professionBilan.ProfessionId;
                                    bilanProfession.AcquiredBehaviouralKnowledge = professionBilan.AcquiredBehaviouralKnowledge?.Trim();
                                    bilanProfession.AcquiredKnowledge = professionBilan.AcquiredKnowledge?.Trim();
                                    bilanProfession.AcquiredKnowHow = professionBilan.AcquiredKnowHow?.Trim();
                                    bilanProfession.BehaviouralKnowledgeToDevelop = professionBilan.BehaviouralKnowledgeToDevelop?.Trim();
                                    bilanProfession.KnowHowToDevelop = professionBilan.KnowHowToDevelop?.Trim();
                                    bilanProfession.KnowledgeToDevelop = professionBilan.KnowledgeToDevelop?.Trim();


                                    bilan.BilanProfessions.Insert(indexOf, bilanProfession);
                                }
                            }
                            else
                            {
                                if (professionBilan.ProfessionId != null)
                                {
                                    var profession = _repository.Profession.Get((int)professionBilan.ProfessionId);

                                    if (profession != null)
                                    {
                                        bilan.CreateBilanProfession(profession,
                                            professionBilan.AcquiredKnowledge.Trim(),
                                            professionBilan.AcquiredBehaviouralKnowledge.Trim(),
                                            professionBilan.AcquiredKnowHow.Trim(),
                                            professionBilan.KnowledgeToDevelop.Trim(),
                                            professionBilan.BehaviouralKnowledgeToDevelop.Trim(),
                                            professionBilan.KnowHowToDevelop.Trim());
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

                _repository.Assessment.Persist(bilan);

                return bilan.Id;
            }
        }

    }
}