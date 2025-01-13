using System.Globalization;
using System.Text;
using ClientManagement.Application.Common.Interfaces;
using ClientManagement.Application.Configuration;
using ClientManagement.Application.Models;
using ClientManagement.Core.Entities;
using ClientManagement.Core.ValueObjects;
using DinkToPdf;
using DinkToPdf.Contracts;
using Fluid;
using Microsoft.Extensions.Options;

namespace ClientManagement.Application.Common.Services
{
    public class DocumentGeneratorService: IDocumentGeneratorService
    {
        private readonly FluidServiceConfiguration Configuration;
        private readonly IConverter Converter;
        private TemplateContext Context;

        public DocumentGeneratorService(IOptionsSnapshot<FluidServiceConfiguration> configurationAccessor, IConverter converter)
        {
            Converter = converter;
            Configuration = configurationAccessor.Value;

            Context = new TemplateContext();
            Context.CultureInfo = new CultureInfo("fr-BE"); //For dates and numbers formatting
            Context.Options.MemberAccessStrategy.Register<AssessmentReportDocumentModel>();
            Context.Options.MemberAccessStrategy.Register<Client>();
            Context.Options.MemberAccessStrategy.Register<Phone>();
            Context.Options.MemberAccessStrategy.Register<Email>();
            Context.Options.MemberAccessStrategy.Register<Assessment>();
            Context.Options.MemberAccessStrategy.Register<Support>();
            Context.Options.MemberAccessStrategy.Register<SocialWorker>();
            Context.Options.MemberAccessStrategy.Register<Training>();
            Context.Options.MemberAccessStrategy.Register<School>();
            Context.Options.MemberAccessStrategy.Register<SchoolRegistration>();
            Context.Options.MemberAccessStrategy.Register<Profession>();
            Context.Options.MemberAccessStrategy.Register<ProfessionalAssessment>();
        }


        public async Task<byte[]> GenerateAssessmentReportAsync(AssessmentReportDocumentModel assessmentReportDocumentModel)
        {
            var htmlRendered = await Task.Run(() => RenderBilanReportHTML(assessmentReportDocumentModel));
            var pdfContent = await Task.Run(() => ConvertHTMLToPDF(htmlRendered));
            return pdfContent;
        }

        private string RenderBilanReportHTML(AssessmentReportDocumentModel assessmentReportDocumentModel)
        {
            var parser = new FluidParser();
            var source = File.ReadAllText(Configuration.BilanTemplateFilePath);

            Context.SetValue("bilanModel", assessmentReportDocumentModel);

            var result = string.Empty;

            if (parser.TryParse(source, out var template))
                result = template.Render(Context);

            return result;
        }

        private byte[] ConvertHTMLToPDF(string htmlContent)
        {
            var globalSettings = new GlobalSettings
            {
                ColorMode = ColorMode.Color,
                Orientation = Orientation.Landscape,
                PaperSize = PaperKind.A4,
                Margins = new MarginSettings(10, 10, 10, 10),
            };

            var objectSettings = new ObjectSettings
            {
                PagesCount = true,
                HtmlContent = htmlContent,
                WebSettings = { DefaultEncoding = Encoding.UTF8.WebName },
                HeaderSettings = { FontSize = 10, Right = "Page [page] / [toPage]", Spacing = 2.812 }
            };

            var pdf = new HtmlToPdfDocument
            {
                GlobalSettings = globalSettings,
                Objects = { objectSettings }
            };

            return Converter.Convert(pdf);
        }
    }
}
