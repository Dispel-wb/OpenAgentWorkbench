/* Read-only document surface. A sandboxed frame never receives Host credentials. */
window.WorkbenchDocumentUi = {
  data() { return { documentPreview: {open:false,path:'',name:'',kind:'',html:'',text:'',url:'',view:'preview',loading:false,error:'',notice:''}, documentPreviewSerial:0 }; },
  methods: {
    async openLocalFile(path) {
      const extension=(String(path||'').match(/\.([^.\/]+)$/)||[])[1]?.toLowerCase()||'';
      const office=['docx','xlsx','pptx'].includes(extension),pdf=extension==='pdf',image=['png','jpg','jpeg','webp','gif','bmp'].includes(extension),html=['html','htm'].includes(extension),text=['txt','md','csv','json','js','ts','tsx','jsx','vue','cs','py','ps1','java','go','rs','cpp','c','h','css','xml','yaml','yml','sql','log'].includes(extension);
      if (!office&&!pdf&&!image&&!html&&!text) return this.openSystemFile(path);
      this.rememberDialogFocus('document');
      this.releaseDocumentUrl();
      const serial=++this.documentPreviewSerial;
      this.cancelDocumentRequest();
      const controller=new AbortController();this.documentRequestController=controller;
      this.documentPreview={open:true,path,name:this.attachmentName(path),kind:'',html:'',text:'',url:'',view:'preview',loading:true,error:'',notice:''};
      this.focusDialog('documentDialog');
      try {
        if(office||pdf){
          const info=await this.api('/api/files/document?path='+encodeURIComponent(path),{signal:controller.signal});
          if(serial!==this.documentPreviewSerial)return;
          Object.assign(this.documentPreview,info);
        } else {
          this.documentPreview.kind=image?'image':html?'html':'text';
          this.documentPreview.notice=image?'图片按原始比例显示。':html?'安全预览不会执行脚本；需要完整交互时请使用系统应用打开。':'只读文本预览；编辑请在文件工作台或系统编辑器中进行。';
        }
        if(pdf||image){
          const timer=setTimeout(()=>controller.abort(),30000);
          try {
            const response=await fetch('/api/files/preview?path='+encodeURIComponent(path),{signal:controller.signal});
            if(!response.ok)throw new Error('文件预览失败：HTTP '+response.status);
            const blob=await response.blob();
            if(serial!==this.documentPreviewSerial)return;
            this.documentPreview.url=URL.createObjectURL(blob);
          } finally { clearTimeout(timer); }
        } else if(html||text){
          const response=await fetch('/api/files/preview?path='+encodeURIComponent(path),{signal:controller.signal});
          if(!response.ok)throw new Error('文件预览失败：HTTP '+response.status);
          const content=await response.text();
          if(serial!==this.documentPreviewSerial)return;
          this.documentPreview.text=content;
        }
      } catch(error) { if(serial===this.documentPreviewSerial)this.documentPreview.error=error.message; }
      finally { if(serial===this.documentPreviewSerial)this.documentPreview.loading=false;if(this.documentRequestController===controller)this.documentRequestController=null; }
    },
    async openSystemFile(path){try{await this.api('/api/files/open',{method:'POST',body:{path}});}catch(error){this.status='打开文件失败：'+error.message;}},
    releaseDocumentUrl(){if(this.documentPreview.url)URL.revokeObjectURL(this.documentPreview.url);},
    cancelDocumentRequest(){this.documentRequestController?.abort();this.documentRequestController=null;},
    closeDocumentPreview(){++this.documentPreviewSerial;this.cancelDocumentRequest();this.releaseDocumentUrl();this.documentPreview.open=false;this.documentPreview.loading=false;this.documentPreview.url='';this.documentPreview.html='';this.documentPreview.text='';this.restoreDialogFocus('document');},
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
