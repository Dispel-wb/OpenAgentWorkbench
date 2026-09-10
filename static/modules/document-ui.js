/* Read-only document surface. A sandboxed frame never receives Host credentials. */
window.WorkbenchDocumentUi = {
  data() { return { documentPreview: {open:false,path:'',name:'',kind:'',html:'',url:'',loading:false,error:'',notice:''}, documentPreviewSerial:0 }; },
  methods: {
    async openLocalFile(path) {
      if (!/\.(pdf|docx|xlsx|pptx)$/i.test(path||'')) return this.openSystemFile(path);
      this.rememberDialogFocus('document');
      this.releaseDocumentUrl();
      const serial=++this.documentPreviewSerial;
      this.cancelDocumentRequest();
      const controller=new AbortController();this.documentRequestController=controller;
      this.documentPreview={open:true,path,name:this.attachmentName(path),kind:'',html:'',url:'',loading:true,error:'',notice:''};
      this.focusDialog('documentDialog');
      try {
        const info=await this.api('/api/files/document?path='+encodeURIComponent(path),{signal:controller.signal});
        if(serial!==this.documentPreviewSerial)return;
        Object.assign(this.documentPreview,info);
        if(info.kind==='pdf'){
          const timer=setTimeout(()=>controller.abort(),30000);
          try {
            const response=await fetch('/api/files/preview?path='+encodeURIComponent(path),{signal:controller.signal});
            if(!response.ok)throw new Error('PDF 读取失败：HTTP '+response.status);
            const blob=await response.blob();
            if(serial!==this.documentPreviewSerial)return;
            this.documentPreview.url=URL.createObjectURL(blob);
          } finally { clearTimeout(timer); }
        }
      } catch(error) { if(serial===this.documentPreviewSerial)this.documentPreview.error=error.message; }
      finally { if(serial===this.documentPreviewSerial)this.documentPreview.loading=false;if(this.documentRequestController===controller)this.documentRequestController=null; }
    },
    async openSystemFile(path){try{await this.api('/api/files/open',{method:'POST',body:{path}});}catch(error){this.status='打开文件失败：'+error.message;}},
    releaseDocumentUrl(){if(this.documentPreview.url)URL.revokeObjectURL(this.documentPreview.url);},
    cancelDocumentRequest(){this.documentRequestController?.abort();this.documentRequestController=null;},
    closeDocumentPreview(){++this.documentPreviewSerial;this.cancelDocumentRequest();this.releaseDocumentUrl();this.documentPreview.open=false;this.documentPreview.loading=false;this.documentPreview.url='';this.documentPreview.html='';this.restoreDialogFocus('document');},
    selectionMenuKey(event){
      const items=[...this.$refs.selectionMenu.querySelectorAll('button')],index=items.indexOf(document.activeElement);
      if(['ArrowRight','ArrowDown','ArrowLeft','ArrowUp','Home','End'].includes(event.key)){
        event.preventDefault();const next=event.key==='Home'?0:event.key==='End'?items.length-1:(index+(event.key==='ArrowLeft'||event.key==='ArrowUp'?-1:1)+items.length)%items.length;items[next]?.focus();
      } else if(event.key==='Escape'||event.key==='Tab'){
        event.preventDefault();event.stopPropagation();this.contextMenu.show=false;this.restoreDialogFocus('selection');
      }
    },
    keyboardSelectionMenu(message,event){
      if(event.key==='ContextMenu'||(event.shiftKey&&event.key==='F10')){this.openSelectionMenu(message,event);}
    }
  },
  beforeUnmount(){++this.documentPreviewSerial;this.cancelDocumentRequest();this.releaseDocumentUrl();}
};
