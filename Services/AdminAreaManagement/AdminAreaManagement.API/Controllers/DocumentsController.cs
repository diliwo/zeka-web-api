using AdminAreaManagement.Application.DocumentPartners.Commands.Delete;
using AdminAreaManagement.Application.DocumentPartners.Commands.Persist;
using AdminAreaManagement.Application.DocumentPartners.Queries;
using AdminAreaManagement.Application.DocumentPartners.Queries.GetDocument;
using AdminAreaManagement.Application.DocumentPartners.Queries.GetDocumentsList;
using Microsoft.AspNetCore.Mvc;

namespace AdminAreaManagement.API.Controllers
{
    public class DocumentsController : ApiControllerBase
    {
        [HttpGet]
        public async Task<ActionResult<DocumentPartnerDto>> GetAll()
        {
            var vm = await Mediator.Send(new GetDocumentsPartnerListQuery());
            return Ok(vm);
        }

        [HttpPost("partner/import")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesDefaultResponseType]
        public async Task<ActionResult> Import(PersistDocumentPartnerCommand command)
        {
            var test = command.Name;
            var id = await Mediator.Send(command);
            return Ok(id);
        }

        [HttpGet("{partnerId}/{jobId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<DocumentPartnersListDto>> GetDocumentsByJobIAndPartnerId(int partnerId, int jobId)
        {
            var vm = await Mediator.Send(new GetDocumentsJobPartnerListQuery() { PartnerId = partnerId, JobId = jobId});

            return Ok(vm);
        }


        [HttpGet("{partnerId}/{jobId}/{documentId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<DocumentPartnersListDto>> GetSelectedDocument(int partnerId, int jobId, int documentId)
        {
            var vm = await Mediator.Send(new GetSelectedDocumentQuery() { PartnerId = partnerId, JobId = jobId, DocumentId = documentId});

            FileContentResult file = new FileContentResult(vm.Data, MimeMapping.MimeUtility.GetMimeMapping(vm.Name));
            file.FileDownloadName = vm.Name;
            return file;
        }

        [Produces(typeof(void))]
        [HttpDelete("{id}")]
        public async Task<ActionResult> DeleteDocument(int id)
        {
            await Mediator.Send(new DeleteDocumentPartnerCommand(id));
            return NoContent();
        }
    }
}
