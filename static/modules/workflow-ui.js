window.WorkbenchWorkflowUi={
  data(){return{workflowDialog:{open:false,busy:false,error:'',items:[],name:'Agent 工作流',nodes:[],maxParallel:2},workflowPoll:null,workflowRefreshing:false};},
  methods:{
    async openWorkflows(parentRunId=''){
      this.rememberDialogFocus('workflows');this.workflowDialog.open=true;this.workflowDialog.error='';
      if(parentRunId||!this.workflowDialog.nodes.length)this.workflowDialog.nodes=[{id:'step1',parentRunId:parentRunId||this.activeJob||'',name:'',prompt:'',dependencyText:'',includeDependencyResults:false}];
      this.focusDialog('workflowDialog');await this.refreshWorkflows();
      clearInterval(this.workflowPoll);this.workflowPoll=setInterval(()=>this.refreshWorkflows(),2500);
    },
    closeWorkflows(){clearInterval(this.workflowPoll);this.workflowPoll=null;this.workflowDialog.open=false;this.restoreDialogFocus('workflows');},
    async refreshWorkflows(){if(this.workflowRefreshing)return;this.workflowRefreshing=true;try{this.workflowDialog.items=await this.api('/api/workflows',{deferConnectionFailure:true});}catch(error){this.workflowDialog.error=error.message;}finally{this.workflowRefreshing=false;}},
    addWorkflowNode(){if(this.workflowDialog.nodes.length>=32)return;const index=this.workflowDialog.nodes.length+1;this.workflowDialog.nodes.push({id:'step'+index,parentRunId:this.workflowDialog.nodes[0]?.parentRunId||'',name:'',prompt:'',dependencyText:'step'+(index-1),includeDependencyResults:false});},
    async submitWorkflow(){
      if(this.workflowDialog.busy)return;this.workflowDialog.busy=true;this.workflowDialog.error='';
      try{await this.api('/api/workflows',{method:'POST',body:{name:this.workflowDialog.name,maxParallel:this.workflowDialog.maxParallel,nodes:this.workflowDialog.nodes.map(n=>({...n,dependencies:n.dependencyText.split(/[,，\s]+/).filter(Boolean)}))}});this.status='工作流已交给后台；关闭窗口不会停止任务';await this.refreshWorkflows();}
      catch(error){this.workflowDialog.error=error.message;}finally{this.workflowDialog.busy=false;}
    },
    async controlWorkflow(item,action){try{await this.api('/api/workflows',{method:'POST',body:{id:item.id,action}});await this.refreshWorkflows();}catch(error){this.workflowDialog.error=error.message;}},
    workflowState(state){return({pending:'等待依赖',dispatching:'派发中',running:'运行中',completed:'已完成',failed:'失败',blocked:'依赖失败',attention:'需核对现场',cancelled:'已取消'})[state]||state;}
  },
  beforeUnmount(){clearInterval(this.workflowPoll);}
};
