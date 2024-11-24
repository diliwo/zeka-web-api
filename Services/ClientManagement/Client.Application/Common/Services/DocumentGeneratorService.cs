using System.Globalization;
using System.Text;
using Client.Application.Common.Interfaces;
using Client.Application.Configuration;
using Client.Application.Models;
using Client.Core.Entities;
using Client.Core.ValueObjects;
using DinkToPdf;
using DinkToPdf.Contracts;
using Fluid;
using Microsoft.Extensions.Options;

namespace Client.Application.Common.Services
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
            Context.Options.MemberAccessStrategy.Register<BilanReportDocumentModel>();
            Context.Options.MemberAccessStrategy.Register<Core.Entities.Client>();
            Context.Options.MemberAccessStrategy.Register<Phone>();
            Context.Options.MemberAccessStrategy.Register<Email>();
            Context.Options.MemberAccessStrategy.Register<Assessment>();
            Context.Options.MemberAccessStrategy.Register<Track>();
            Context.Options.MemberAccessStrategy.Register<Staff>();
            Context.Options.MemberAccessStrategy.Register<Service>();
            Context.Options.MemberAccessStrategy.Register<Training>();
            Context.Options.MemberAccessStrategy.Register<School>();
            Context.Options.MemberAccessStrategy.Register<SchoolEnrollment>();
            Context.Options.MemberAccessStrategy.Register<Profession>();
            Context.Options.MemberAccessStrategy.Register<ProfessionalAssessment>();
        }


        public async Task<byte[]> GenerateBilanReportAsync(BilanReportDocumentModel bilanReportDocumentModel)
        {
            var htmlRendered = await Task.Run(() => RenderBilanReportHTML(bilanReportDocumentModel));
            var pdfContent = await Task.Run(() => ConvertHTMLToPDF(htmlRendered));
            return pdfContent;
        }

        private string RenderBilanReportHTML(BilanReportDocumentModel bilanReportDocumentModel)
        {
            var parser = new FluidParser();
            var source = File.ReadAllText(Configuration.BilanTemplateFilePath);

            Context.SetValue("bilanModel", bilanReportDocumentModel);

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
