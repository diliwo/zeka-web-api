namespace Client.Core.Common.Dto
{
    public class AppointmentDto
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public int AgendaId { get; set; }
        public int AppointmentTypeId { get; set; }
        public int UsagerId { get; set; }
        public int? BookingId { get; set; }
        public DateTime StartDate { get; set; }
        public int Duration { get; set; }
        public string Observation { get; set; }
        public string NumDossier { get; set; }
        public string Niss { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string PhoneNumber { get; set; }
        public int SoftDelete { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime LastModifiedDate { get; set; }
        public DateTime? ArrivalTime { get; set; }
        public string TicketNumber { get; set; }
        public string CheckedInObservation { get; set; }
        public DateTime? BlockedTime { get; set; }
        public string BlockedObservations { get; set; }
        public DateTime? ClosingTime { get; set; }
        public string ClosingObservation { get; set; }
        public int? ReplacementAgendaId { get; set; }
        public int? ReplacementBookingId { get; set; }
        public int? IndividuId { get; set; }
    }
}
