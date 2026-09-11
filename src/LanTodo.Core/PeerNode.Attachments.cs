namespace LanTodo.Core;

public sealed partial class PeerNode
{
    private async Task AttachmentScanLoop(CancellationToken token)
    {
        while(!token.IsCancellationRequested)
        {
            try { if(store is TodoStore files) files.DetectMissingAttachments(); await Task.Delay(2000,token); }
            catch(OperationCanceledException) when(token.IsCancellationRequested) { break; }
            catch(IOException) { await DelayRetry(token); }
        }
    }
    private Packet HandleAttachment(Packet request, HashSet<Attachment> index)
    {
        if (store is not TodoStore files || request.Attachment is not { } item) throw new InvalidDataException("附件请求无效。");
        item.Validate(); files.DetectMissingAttachments();
        // Authorization to sync is not authorization to write arbitrary unreferenced files.
        if (!files.ActiveAttachments().Contains(item)) throw new InvalidDataException("未知或已删除附件。");
        switch (request.Kind)
        {
            case "blob-status": return new("blob-status", Done: files.Attachments.Has(item), Position: files.Attachments.Received(item));
            case "blob-get": return new("blob-data", Position: request.Position, Bytes: files.Attachments.ReadChunk(item, request.Position));
            case "blob-put":
                var existed = files.Attachments.Has(item);
                files.ReceiveAttachment(item, request.Position, request.Bytes ?? throw new InvalidDataException("附件数据缺失。"));
                if (!existed && files.Attachments.Has(item)) files.NotifyAttachmentsChanged();
                return new("blob-saved", Position: files.Attachments.Received(item));
            default: throw new InvalidDataException();
        }
    }
    private static async Task SyncAttachments(TodoStore files, Func<Packet, Task<Packet>> exchange, CancellationToken token)
    {
        files.DetectMissingAttachments();
        foreach(var batch in files.Attachments.InvalidKeys.Chunk(256))
        {
            var reply=await exchange(new("blob-invalidations",Ids:batch));
            if(reply.Kind!="blob-invalidations")throw new InvalidDataException("附件失效记录无效。");
        }
        for(int offset=0;;offset+=256)
        {
            var reply=await exchange(new("blob-invalidations",Offset:offset));
            if(reply.Kind!="blob-invalidations" || reply.Ids is null || reply.Ids.Length>256 || (!reply.Done && reply.Ids.Length!=256))throw new InvalidDataException("附件失效记录无效。");
            files.Attachments.Invalidate(reply.Ids);
            if(reply.Done)break;
        }
        bool unavailable = false;
        foreach (var item in files.ActiveAttachments().Where(a=>!files.Attachments.IsInvalid(a)).DistinctBy(a => a.Hash))
        {
            token.ThrowIfCancellationRequested();
            if(files.Attachments.IsInvalid(item))continue;
            var remote = await exchange(new("blob-status", Attachment: item));
            if (remote.Kind != "blob-status" || remote.Position < 0 || remote.Position > item.Size) throw new InvalidDataException("附件状态无效。");
            if (!files.Attachments.Has(item) && remote.Done)
            {
                long offset = files.Attachments.Received(item);
                do
                {
                    var reply = await exchange(new("blob-get", Attachment: item, Position: offset));
                    if (reply.Kind != "blob-data" || reply.Position != offset || reply.Bytes is null) throw new InvalidDataException("附件响应无效。");
                    files.ReceiveAttachment(item, offset, reply.Bytes);
                    offset += reply.Bytes.Length;
                } while (offset < item.Size);
                files.NotifyAttachmentsChanged();
            }
            else if (files.Attachments.Has(item) && !remote.Done)
            {
                long offset = remote.Position;
                do
                {
                    var bytes = files.Attachments.ReadChunk(item, offset);
                    var reply = await exchange(new("blob-put", Attachment: item, Position: offset, Bytes: bytes));
                    if (reply.Kind != "blob-saved" || reply.Position < offset + bytes.Length || reply.Position > item.Size) throw new InvalidDataException("附件尚未确认保存。");
                    offset = reply.Position;
                } while (offset < item.Size);
            }
            else if (!files.Attachments.Has(item)) unavailable = true;
        }
        if (unavailable) throw new IOException("部分附件原件暂不可用，请连接持有原件的设备后重试；其他可用附件已同步。");
    }
}
